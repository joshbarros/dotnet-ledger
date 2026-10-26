using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ledger.Payments;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a payment forward. Each step runs only from the status it expects.</summary>
public class PaymentsHandler(PaymentsDbContext db, IOptions<PaymentOptions> options) : IMessageHandler
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
