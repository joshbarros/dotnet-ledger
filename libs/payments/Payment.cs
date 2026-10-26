namespace Ledger.Payments;

public enum PaymentStatus { Requested, Held, Settled, Reversed, Rejected }

public class Payment
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public long AmountCents { get; set; }
    public long FeeCents { get; set; }
    public PaymentStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ReceiptNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FundsReleasedAt { get; set; }
}

public class PaymentOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreatePaymentRequest(string? AccountCode, string? Reference, long AmountCents);

public record PaymentDto(
    Guid Id, Guid OwnerId, string AccountCode, string Reference, long AmountCents, long FeeCents, string Status,
    string? Reason, string? ReceiptNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? FundsReleasedAt)
{
    public static PaymentDto From(Payment b) => new(
        b.Id, b.OwnerId, b.AccountCode, b.Reference, b.AmountCents, b.FeeCents, b.Status.ToString(),
        b.Reason, b.ReceiptNumber, b.CreatedAt, b.UpdatedAt, b.FundsReleasedAt);
}
