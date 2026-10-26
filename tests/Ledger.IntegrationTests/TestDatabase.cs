using Ledger.Payments;
using Ledger.Accounts;
using Ledger.Receipts;
using Ledger.Shared.Auth;
using Ledger.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ledger.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LEDGER_TEST_DB")
        ?? "Host=localhost;Port=5472;Database=ledger_test;Username=ledger;Password=ledger";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static PaymentsDbContext Payments() => new(Options<PaymentsDbContext>(PaymentsDbContext.SchemaName));
    public static AccountsDbContext Accounts() => new(Options<AccountsDbContext>(AccountsDbContext.SchemaName));
    public static ReceiptsDbContext Receipts() => new(Options<ReceiptsDbContext>(ReceiptsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var payments = Payments();
        await using var funds = Accounts();
        await using var receipts = Receipts();
        await payments.Database.MigrateAsync();
        await funds.Database.MigrateAsync();
        await receipts.Database.MigrateAsync();
        await payments.Database.ExecuteSqlRawAsync(
            "TRUNCATE payments.\"InboxMessages\", payments.\"OutboxMessages\", payments.\"Payments\", " +
            "funds.\"InboxMessages\", funds.\"OutboxMessages\", funds.\"FundHolds\", funds.\"Accounts\", " +
            "receipts.\"InboxMessages\", receipts.\"OutboxMessages\", receipts.\"Receipts\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Account> AddAccountAsync(long openingCents)
    {
        await using var db = Accounts();
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

    public static async Task<Payment> AddPaymentAsync(Account account, string reference, long amountCents, PaymentStatus status, Guid? ownerId = null)
    {
        await using var db = Payments();
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Account[] accounts) : IAccountCatalog
{
    public Task<AccountInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(accounts.Where(s => s.Code == code).Select(s => new AccountInfo(s.Id, s.Code)).FirstOrDefault());
}
