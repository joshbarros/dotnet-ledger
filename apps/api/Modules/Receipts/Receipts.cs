using Ledger.Api.Data;
using Ledger.Api.Messaging;
using Ledger.Api.Modules.Payments;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Modules.Receipts;

public enum ReceiptStatus { Issued, Rejected }

public class Receipt
{
    public Guid PaymentId { get; set; }
    public string? Number { get; set; }
    public ReceiptStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the receipt once the funds are held. A poison reference is committed as rejected.</summary>
public class ReceiptsHandler(LedgerDbContext db) : IMessageHandler
{
    public const string Name = "receipts";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Receipts does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<FundsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, FundsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PaymentRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Receipts.Add(new Receipt
            {
                PaymentId = held.PaymentId,
                Status = ReceiptStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.ReleaseKey, held.PaymentId, new ReleaseFunds(held.PaymentId, "poison"));
            db.Enqueue("receipts", Topology.RejectedKey, held.PaymentId, new ReceiptRejected(held.PaymentId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"RC-{held.PaymentId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Receipts.Add(new Receipt
            {
                PaymentId = held.PaymentId,
                Number = number,
                Status = ReceiptStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.IssuedKey, held.PaymentId, new ReceiptIssued(held.PaymentId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
