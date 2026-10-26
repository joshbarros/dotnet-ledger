namespace Ledger.Payments;

public static class PaymentRules
{
    /// <summary>$50,000.00 per payment.</summary>
    public const long MaxPaymentCents = 5_000_000;
    public const int MaxReferenceLength = 40;
    public const long FlatFeeCents = 50;
    public const long FeeBasisPoints = 50;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long amountCents)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Payment reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Payment reference must be at most {MaxReferenceLength} characters.";
        if (amountCents < 1)
            return "A payment needs at least 1 cent.";
        if (amountCents > MaxPaymentCents)
            return $"A payment is at most {MaxPaymentCents} cents ($50,000.00).";
        return null;
    }

    /// <summary>A flat 50 cents plus 0.5% of the amount, truncated to a whole cent. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(long amountCents) => checked(FlatFeeCents + amountCents * FeeBasisPoints / 10_000);
}
