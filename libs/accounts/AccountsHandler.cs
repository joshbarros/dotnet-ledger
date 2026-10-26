using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Accounts;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Account.AvailableCents and of fund holds.
/// The reservation is one conditional UPDATE: it matches only while the available cents still cover the request.
/// </summary>
public class AccountsHandler(AccountsDbContext db) : IMessageHandler
{
    public const string Name = "accounts";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveFunds>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseFunds>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Accounts does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveFunds command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Accounts
            .Where(s => s.Id == command.AccountId && s.AvailableCents >= command.AmountCents)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableCents, x => x.AvailableCents - command.AmountCents), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new FundHold
            {
                PaymentId = command.PaymentId,
                AccountId = command.AccountId,
                AmountCents = command.AmountCents,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("accounts", Topology.HeldKey, command.PaymentId,
                new FundsHeld(command.PaymentId, command.OwnerId, command.AccountId, command.AmountCents, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Accounts.Where(s => s.Id == command.AccountId)
                .Select(s => s.AvailableCents).FirstOrDefaultAsync(ct);
            db.Enqueue("accounts", Topology.ShortKey, command.PaymentId,
                new FundsShort(command.PaymentId, command.Reference, command.AmountCents, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held funds back exactly once. A second release for the same payment changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseFunds command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.PaymentId == command.PaymentId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.PaymentId == command.PaymentId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Accounts
                    .Where(s => s.Id == hold.AccountId && s.AvailableCents + hold.AmountCents <= s.OpeningCents)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableCents, x => x.AvailableCents + hold.AmountCents), ct);
                db.Enqueue("accounts", Topology.ReleasedKey, command.PaymentId,
                    new FundsReleased(command.PaymentId, hold.AmountCents, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
