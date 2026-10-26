using Ledger.Payments;
using Ledger.Receipts;
using Ledger.Shared;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ledger.IntegrationTests;

public class SagaTests
{
    private static PaymentsHandler Handler(PaymentsDbContext db) => new(db, Options.Create(new PaymentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var payment = await TestDatabase.AddPaymentAsync(account, "PAY-POISON-1", 2, PaymentStatus.Held);
        var held = new FundsHeld(payment.Id, payment.OwnerId, account.Id, 2, payment.Reference);

        await using var docs = TestDatabase.Receipts();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new ReceiptsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Receipts();
        var doc = await check.Receipts.SingleAsync(d => d.PaymentId == payment.Id);
        Assert.Equal(ReceiptStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(payment.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesPaymentReversedAndReceiptIssued()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var owner = Guid.CreateVersion7();
        var payment = await TestDatabase.AddPaymentAsync(account, "PAY-1", 2, PaymentStatus.Held, owner);

        // The receipt is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Receipts())
            await new ReceiptsHandler(docs).IssueAsync(Guid.CreateVersion7(), new FundsHeld(payment.Id, owner, account.Id, 2, payment.Reference));

        // The owner reverses first.
        await using (var payments = TestDatabase.Payments())
            await new PaymentService(payments, new FixedCatalog(account), new TestUser(owner)).ReverseAsync(payment.Id, default);

        await using var late = TestDatabase.Payments();
        var mark = await Handler(late).MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(payment.Id, "RC-TEST"));

        await using var check = TestDatabase.Payments();
        await using var checkDocs = TestDatabase.Receipts();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(PaymentStatus.Reversed, (await check.Payments.SingleAsync()).Status);
        Assert.Equal(ReceiptStatus.Issued, (await checkDocs.Receipts.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var held = await TestDatabase.AddPaymentAsync(account, "PAY-A", 1, PaymentStatus.Held);
        var rejected = await TestDatabase.AddPaymentAsync(account, "PAY-B", 1, PaymentStatus.Rejected);
        var requested = await TestDatabase.AddPaymentAsync(account, "PAY-C", 1, PaymentStatus.Requested);

        await using (var a = TestDatabase.Payments())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(held.Id, "RC-1")));
        await using (var b = TestDatabase.Payments())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(rejected.Id, "RC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Payments())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkSettledAsync(Guid.CreateVersion7(), new ReceiptIssued(requested.Id, "RC-3")));

        await using var check = TestDatabase.Payments();
        var statuses = (await check.Payments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(PaymentStatus.Settled, statuses["PAY-A"]);
        Assert.Equal(PaymentStatus.Rejected, statuses["PAY-B"]);
        Assert.Equal(PaymentStatus.Requested, statuses["PAY-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedPayment_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var owner = Guid.CreateVersion7();
        var payment = await TestDatabase.AddPaymentAsync(account, "PAY-1", 1, PaymentStatus.Rejected, owner);

        await using var db = TestDatabase.Payments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PaymentService(db, new FixedCatalog(account), new TestUser(owner)).ReverseAsync(payment.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Reversal_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var payment = await TestDatabase.AddPaymentAsync(account, "PAY-1", 1, PaymentStatus.Held);

        await using var db = TestDatabase.Payments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PaymentService(db, new FixedCatalog(account), new TestUser(Guid.NewGuid())).ReverseAsync(payment.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Payments();
        Assert.Equal(PaymentStatus.Held, (await check.Payments.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanReverseAnyonesPayment()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var payment = await TestDatabase.AddPaymentAsync(account, "PAY-1", 1, PaymentStatus.Held);

        await using var db = TestDatabase.Payments();
        var reversed = await new PaymentService(db, new FixedCatalog(account), new TestUser(Guid.NewGuid(), staff: true)).ReverseAsync(payment.Id, default);
        Assert.Equal(PaymentStatus.Reversed, reversed.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Payments();
        var payment = await new PaymentService(db, new FixedCatalog(account), new TestUser(owner))
            .CreateAsync(new CreatePaymentRequest(account.Code, "PAY-1", 2), default);

        await using var check = TestDatabase.Payments();
        Assert.Equal(owner, (await check.Payments.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveFunds>(message.Payload).OwnerId);
        Assert.Equal(PaymentStatus.Requested, payment.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var account = await TestDatabase.AddAccountAsync(5);
        await using var db = TestDatabase.Payments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PaymentService(db, new FixedCatalog(account), new TestUser(null)).CreateAsync(new CreatePaymentRequest(account.Code, "PAY-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownAccount_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Payments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new PaymentService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreatePaymentRequest("NOPE", "PAY-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
