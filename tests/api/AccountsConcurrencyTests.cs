using System.Collections.Concurrent;
using Ledger.Api.Messaging;
using Ledger.Api.Modules.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ledger.Api.Tests;

public class AccountsConcurrencyTests
{
    [Fact]
    public async Task LastFunds_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, openingCents: 10_100);

        var outcomes = new ConcurrentBag<(Guid Payment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var paymentId = Guid.CreateVersion7();
            var handler = new AccountsHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveFunds(paymentId, account.Id, 10_100, $"PAY-{n}"));
            outcomes.Add((paymentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, openingCents: 40_000);
        var paymentId = Guid.CreateVersion7();
        var handler = new AccountsHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveFunds(paymentId, account.Id, 30_000, "PAY-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same funds back.
        var first = await new AccountsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseFunds(paymentId, "poison"));
        var second = await new AccountsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseFunds(paymentId, "reversal"));

        var remaining = await TestDatabase.Create().Accounts.Where(s => s.Id == account.Id).Select(s => s.AvailableCents).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(40_000, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, openingCents: 50_000);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveFunds(Guid.CreateVersion7(), account.Id, 20_000, "PAY-1");

        var first = await new AccountsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new AccountsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Accounts.Where(s => s.Id == account.Id).Select(s => s.AvailableCents).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(30_000, remaining);
    }
}
