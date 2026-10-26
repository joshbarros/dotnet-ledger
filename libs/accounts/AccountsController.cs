using Ledger.Shared;
using Ledger.Shared.Auth;
using Ledger.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Accounts;

public record CreateAccountRequest(string? Code, string? Holder, string? Currency, string? Branch, DateTimeOffset? OpenedAt, long OpeningCents);

public record AccountDto(Guid Id, string Code, string Holder, string Currency, string Branch, DateTimeOffset OpenedAt, long OpeningCents, long AvailableCents)
{
    public static AccountDto From(Account s) => new(s.Id, s.Code, s.Holder, s.Currency, s.Branch, s.OpenedAt, s.OpeningCents, s.AvailableCents);
}

/// <summary>What other modules may ask Accounts, over Accounts's own tables.</summary>
public sealed class AccountCatalog(AccountsDbContext db) : IAccountCatalog
{
    public async Task<AccountInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().Where(s => s.Code == code).Select(s => new AccountInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/accounts")]
[Authorize]
public class AccountsController(AccountsDbContext db) : ControllerBase
{
    private const long MaxOpeningCents = 10_000_000_000; // $100,000,000.00

    [HttpGet]
    public async Task<IEnumerable<AccountDto>> List(CancellationToken ct) =>
        (await db.Accounts.AsNoTracking().OrderBy(s => s.OpenedAt).ThenBy(s => s.Code).ToListAsync(ct)).Select(AccountDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<AccountDto>> Get(string code, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return account is null ? NotFound() : AccountDto.From(account);
    }

    /// <summary>Opens a account. Staff only. This is the only place AvailableCents is initialised; after that only the Accounts handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<AccountDto>> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Holder) || string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.Branch))
            throw new DomainException(400, "Holder, currency and branch are required.");
        if (request.OpeningCents < 1 || request.OpeningCents > MaxOpeningCents)
            throw new DomainException(400, $"Opening balance must be between 1 and {MaxOpeningCents} cents.");
        if (await db.Accounts.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Account '{code}' already exists.");

        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Holder = request.Holder.Trim(),
            Currency = request.Currency.Trim(),
            Branch = request.Branch.Trim(),
            OpenedAt = request.OpenedAt ?? DateTimeOffset.UtcNow.AddDays(14),
            OpeningCents = request.OpeningCents,
            AvailableCents = request.OpeningCents,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return Created($"/api/accounts/{account.Code}", AccountDto.From(account));
    }
}
