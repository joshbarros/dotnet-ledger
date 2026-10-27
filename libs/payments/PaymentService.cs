using Ledger.Shared;
using Ledger.Shared.Auth;
using Ledger.Shared.Contracts;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Payments;

/// <summary>HTTP side of the saga: writes the payment and its first outbox row in one local transaction.</summary>
public class PaymentService(PaymentsDbContext db, IAccountCatalog accounts, ICurrentUser user)
{
    public async Task<Payment> CreateAsync(CreatePaymentRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to send.");
        if (string.IsNullOrWhiteSpace(request.AccountCode))
            throw new DomainException(400, "Account code is required.");
        var error = PaymentRules.Validate(request.Reference, request.AmountCents);
        if (error is not null)
            throw new DomainException(400, error);

        var account = await accounts.FindAsync(request.AccountCode.Trim(), ct)
            ?? throw new DomainException(404, $"Account '{request.AccountCode}' does not exist.");

        var amountCents = request.AmountCents;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("payments", Topology.ReserveKey, payment.Id, new ReserveFunds(payment.Id, ownerId, account.Id, amountCents + payment.FeeCents, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Payment> GetAsync(Guid id, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (payment is null || !OwnerAccess.CanAccess(user, payment.OwnerId))
            throw new DomainException(404, "Payment not found.");
        return payment;
    }

    public async Task<(List<Payment> Items, int Total)> ListAsync(string? accountCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Payments.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see payments.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(accountCode))
            query = query.Where(b => b.AccountCode == accountCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Settled to Reversed and asks Accounts to give the funds back. The receipt is not touched.</summary>
    public async Task<Payment> ReverseAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Payments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A payment in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("payments", Topology.ReleaseKey, id, new ReleaseFunds(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Payments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
