using Ledger.Api.Messaging;
using Ledger.Api.Modules.Payments;
using Ledger.Api.Modules.Accounts;
using Ledger.Api.Modules.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ledger.Api.Tests;

public class SagaTests
{
    private static PaymentsHandler Payments() =>
        new(TestDatabase.Create(), Options.Create(new PaymentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, 5);
        var payment = await TestDatabase.AddPaymentAsync(db, account, "PAY-POISON-1", 2, PaymentStatus.Held);
        var held = new FundsHeld(payment.Id, account.Id, 2, payment.Reference);

        var handler = new ReceiptsHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Receipts.SingleAsync(d => d.PaymentId == payment.Id);
        Assert.Equal(ReceiptStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesPaymentReversedAndReceiptIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, 5);
        var payment = await TestDatabase.AddPaymentAsync(db, account, "PAY-1", 2, PaymentStatus.Held);

        // The receipt is issued; the final mark has not landed yet.
        var number = "RC-TEST";
        await new ReceiptsHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new FundsHeld(payment.Id, account.Id, 2, payment.Reference));

        // The reversal gets there first.
        await new PaymentService(TestDatabase.Create()).ReverseAsync(payment.Id, default);

        var mark = await Payments().MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(payment.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(PaymentStatus.Reversed, (await check.Payments.SingleAsync()).Status);
        Assert.Equal(ReceiptStatus.Issued, (await check.Receipts.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, 5);
        var held = await TestDatabase.AddPaymentAsync(db, account, "PAY-A", 1, PaymentStatus.Held);
        var rejected = await TestDatabase.AddPaymentAsync(db, account, "PAY-B", 1, PaymentStatus.Rejected);
        var requested = await TestDatabase.AddPaymentAsync(db, account, "PAY-C", 1, PaymentStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Payments().MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(held.Id, "RC-1")));
        Assert.Equal(MarkOutcome.Skipped, await Payments().MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(rejected.Id, "RC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Payments().MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(requested.Id, "RC-3")));

        var statuses = (await TestDatabase.Create().Payments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(PaymentStatus.Settled, statuses["PAY-A"]);
        Assert.Equal(PaymentStatus.Rejected, statuses["PAY-B"]);
        Assert.Equal(PaymentStatus.Requested, statuses["PAY-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedPayment_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(db, 5);
        var payment = await TestDatabase.AddPaymentAsync(db, account, "PAY-1", 1, PaymentStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new PaymentService(TestDatabase.Create()).ReverseAsync(payment.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
