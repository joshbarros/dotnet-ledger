using Ledger.Shared;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Receipts;

public enum ReceiptStatus { Issued, Rejected }

public class Receipt
{
    public Guid PaymentId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public ReceiptStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ReceiptsDbContext(DbContextOptions<ReceiptsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "receipts";

    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Receipt>(e =>
        {
            e.ToTable("Receipts");
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the receipt once the funds are held. A poison reference is committed as rejected.</summary>
public class ReceiptsHandler(ReceiptsDbContext db) : IMessageHandler
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
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Receipts.Add(new Receipt
            {
                PaymentId = held.PaymentId,
                OwnerId = held.OwnerId,
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
                OwnerId = held.OwnerId,
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
