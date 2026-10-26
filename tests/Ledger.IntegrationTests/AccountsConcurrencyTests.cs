using System.Collections.Concurrent;
using Ledger.Accounts;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ledger.IntegrationTests;

public class AccountsConcurrencyTests
{
    [Fact]
    public async Task LastFunds_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(openingCents: 1);

        var outcomes = new ConcurrentBag<(Guid Payment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var paymentId = Guid.CreateVersion7();
            await using var db = TestDatabase.Accounts();
            var outcome = await new AccountsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveFunds(paymentId, Guid.NewGuid(), account.Id, 1, $"PAY-{n}"));
            outcomes.Add((paymentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Accounts();
        var remaining = await check.Accounts.Where(s => s.Id == account.Id).Select(s => s.AvailableCents).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Payment;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.PaymentId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheFundsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(openingCents: 4);
        var paymentId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Accounts())
            await new AccountsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveFunds(paymentId, Guid.NewGuid(), account.Id, 3, "PAY-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same funds back.
        await using var first = TestDatabase.Accounts();
        await using var second = TestDatabase.Accounts();
        var one = await new AccountsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseFunds(paymentId, "poison"));
        var two = await new AccountsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseFunds(paymentId, "reversal"));

        await using var check = TestDatabase.Accounts();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Accounts.Where(s => s.Id == account.Id).Select(s => s.AvailableCents).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(openingCents: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveFunds(Guid.CreateVersion7(), Guid.NewGuid(), account.Id, 2, "PAY-1");

        await using var a = TestDatabase.Accounts();
        await using var b = TestDatabase.Accounts();
        var first = await new AccountsHandler(a).ReserveAsync(messageId, command);
        var second = await new AccountsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Accounts();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Accounts.Where(s => s.Id == account.Id).Select(s => s.AvailableCents).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(openingCents: 2);
        await using var db = TestDatabase.Accounts();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Accounts.Where(s => s.Id == account.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableCents, x => x.AvailableCents - 3)));
    }
}
