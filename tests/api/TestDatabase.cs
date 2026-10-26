using Ledger.Api.Data;
using Ledger.Api.Modules.Payments;
using Ledger.Api.Modules.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LEDGER_TEST_DB")
        ?? "Host=localhost;Port=5472;Database=ledger_test;Username=ledger;Password=ledger";

    private static readonly DbContextOptions<LedgerDbContext> Options =
        new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options;

    public static LedgerDbContext Create() => new(Options);

    public static async Task<LedgerDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Receipts\", \"FundHolds\", \"Payments\", \"Accounts\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Account> AddAccountAsync(LedgerDbContext db, long openingCents)
    {
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Holder = "MV Test",
            Currency = "USD",
            Branch = "Main Branch",
            OpenedAt = DateTimeOffset.UtcNow.AddDays(7),
            OpeningCents = openingCents,
            AvailableCents = openingCents,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public static async Task<Payment> AddPaymentAsync(LedgerDbContext db, Account account, string reference, long amountCents, PaymentStatus status)
    {
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            AccountId = account.Id,
            AccountCode = account.Code,
            Reference = reference,
            AmountCents = amountCents,
            FeeCents = PaymentRules.FeeCents(amountCents),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
