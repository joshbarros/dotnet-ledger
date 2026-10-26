using Ledger.Api.Messaging;
using Ledger.Api.Modules.Payments;
using Ledger.Api.Modules.Accounts;
using Ledger.Api.Modules.Receipts;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Data;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FundHold> Holds => Set<FundHold>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Accounts_Remaining", "\"AvailableCents\" >= 0 AND \"AvailableCents\" <= \"OpeningCents\""));
        });

        b.Entity<FundHold>(e =>
        {
            e.ToTable("FundHolds");
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        });

        b.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(PaymentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.AccountId, x.Reference }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        });

        b.Entity<Receipt>(e =>
        {
            e.ToTable("Receipts");
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
