using Ledger.Api.Modules.Payments;
using Xunit;

namespace Ledger.Api.Tests;

public class PaymentRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("PAY-1", 0)]
    [InlineData("PAY-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long amountCents) =>
        Assert.NotNull(PaymentRules.Validate(reference, amountCents));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(PaymentRules.Validate("PAY-1", PaymentRules.MaxPaymentCents));
        Assert.NotNull(PaymentRules.Validate("PAY-1", PaymentRules.MaxPaymentCents + 1));
        Assert.NotNull(PaymentRules.Validate(new string('x', PaymentRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)]
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long amountCents) =>
        Assert.NotNull(PaymentRules.Validate("PAY-1", amountCents));

    [Fact]
    public void FeeArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => PaymentRules.FeeCents(long.MaxValue));
        Assert.Throws<OverflowException>(() => PaymentRules.FeeCents(long.MaxValue / PaymentRules.FeeBasisPoints + 1));
    }

    [Fact]
    public void HappyFee_IsFlatFeePlusHalfAPercent()
    {
        Assert.Null(PaymentRules.Validate("PAY-1", 100_000));
        Assert.Equal(50 + 500, PaymentRules.FeeCents(100_000)); // $1,000.00 -> $5.50
        Assert.Equal(50, PaymentRules.FeeCents(1)); // the percentage truncates to zero
        Assert.Equal(50 + 25_000, PaymentRules.FeeCents(PaymentRules.MaxPaymentCents));
    }

    [Theory]
    [InlineData("POISON")]
    [InlineData("PAY-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PaymentRules.IsPoison(reference));

    [Theory]
    [InlineData("PAY-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PaymentRules.IsPoison(reference));
}
