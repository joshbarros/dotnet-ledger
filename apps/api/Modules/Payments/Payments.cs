using Ledger.Api.Data;
using Ledger.Api.Messaging;
using Ledger.Api.Modules.Accounts;
using Ledger.Api.Modules.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledger.Api.Modules.Payments;

public enum PaymentStatus { Requested, Held, Settled, Reversed, Rejected }

public class Payment
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string AccountCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public long AmountCents { get; set; }
    public long FeeCents { get; set; }
    public PaymentStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ReceiptNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FundsReleasedAt { get; set; }
}

public class PaymentOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreatePaymentRequest(string? AccountCode, string? Reference, long AmountCents);

public record PaymentDto(
    Guid Id, string AccountCode, string Reference, long AmountCents, long FeeCents, string Status,
    string? Reason, string? ReceiptNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? FundsReleasedAt)
{
    public static PaymentDto From(Payment b) => new(
        b.Id, b.AccountCode, b.Reference, b.AmountCents, b.FeeCents, b.Status.ToString(),
        b.Reason, b.ReceiptNumber, b.CreatedAt, b.UpdatedAt, b.FundsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the payment and its first outbox row in one local transaction.</summary>
public class PaymentService(LedgerDbContext db)
{
    public async Task<Payment> CreateAsync(CreatePaymentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccountCode))
            throw new DomainException(400, "Account code is required.");
        var error = PaymentRules.Validate(request.Reference, request.AmountCents);
        if (error is not null)
            throw new DomainException(400, error);

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.AccountCode.Trim(), ct)
            ?? throw new DomainException(404, $"Account '{request.AccountCode}' does not exist.");

        var amountCents = request.AmountCents;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            AccountId = account.Id,
            AccountCode = account.Code,
            Reference = reference,
            AmountCents = amountCents,
            FeeCents = PaymentRules.FeeCents(amountCents),
            Status = PaymentStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Payments.Add(payment);
        db.Enqueue("payments", Topology.ReserveKey, payment.Id, new ReserveFunds(payment.Id, account.Id, amountCents + payment.FeeCents, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already used on {account.Code}.");
        }
        await tx.CommitAsync(ct);
        return payment;
    }

    /// <summary>Moves Held or Settled to Reversed and asks Accounts to give the funds back. The receipt is not touched.</summary>
    public async Task<Payment> ReverseAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Payments
            .Where(b => b.Id == id && (b.Status == PaymentStatus.Held || b.Status == PaymentStatus.Settled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PaymentStatus.Reversed)
                .SetProperty(b => b.Reason, "Reversed by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Payments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Payment not found.");
            throw new DomainException(409, $"A payment in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("payments", Topology.ReleaseKey, id, new ReleaseFunds(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Payments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a payment forward. Each step runs only from the status it expects.</summary>
public class PaymentsHandler(LedgerDbContext db, IOptions<PaymentOptions> options) : IMessageHandler
{
    public const string Name = "payments";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<FundsHeld>(d.Body).PaymentId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<FundsShort>(d.Body), ct),
        Topology.IssuedKey => MarkSettledAsync(d.MessageId, Json.Parse<ReceiptIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<ReceiptRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<FundsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Payments does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid paymentId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Payments
            .Where(b => b.Id == paymentId && b.Status == PaymentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PaymentStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, FundsShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Payments
            .Where(b => b.Id == m.PaymentId && b.Status == PaymentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PaymentStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedCents} cents needed, {m.AvailableCents} available.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a reversal got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkSettledAsync(Guid messageId, ReceiptIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Payments
            .Where(b => b.Id == m.PaymentId && b.Status == PaymentStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PaymentStatus.Settled)
                .SetProperty(b => b.ReceiptNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            paymentIdForOrderCheck: m.PaymentId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, ReceiptRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Payments
            .Where(b => b.Id == m.PaymentId && (b.Status == PaymentStatus.Held || b.Status == PaymentStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, PaymentStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, FundsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Payments
            .Where(b => b.Id == m.PaymentId && b.FundsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.FundsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? paymentIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && paymentIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Payments.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == PaymentStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
