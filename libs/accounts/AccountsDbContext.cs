using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Accounts;

public class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "funds";

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FundHold> Holds => Set<FundHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
