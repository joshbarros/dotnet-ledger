using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Payments;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "payments";

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(PaymentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.AccountId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
