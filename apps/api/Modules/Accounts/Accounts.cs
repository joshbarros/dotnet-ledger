namespace Ledger.Api.Modules.Accounts;

public class Account
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Holder { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Branch { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public long OpeningCents { get; set; }
    public long AvailableCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class FundHold
{
    public Guid PaymentId { get; set; }
    public Guid AccountId { get; set; }
    public long AmountCents { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
