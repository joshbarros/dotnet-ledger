namespace Ledger.Shared.Contracts;

public record AccountInfo(Guid Id, string Code);

/// <summary>
/// What Payments may ask Accounts, without referencing it. Accounts implements it over its own tables;
/// Payments never reads another module's schema.
/// </summary>
public interface IAccountCatalog
{
    Task<AccountInfo?> FindAsync(string code, CancellationToken ct);
}
