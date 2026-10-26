using Ledger.Payments;
using Ledger.Accounts;
using Xunit;

namespace Ledger.UnitTests;

public class DtoTests
{
    [Fact]
    public void PaymentDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            AccountCode = "S-1",
            Reference = "PAY-1",
            AmountCents = 3,
            FeeCents = 277_500,
            Status = PaymentStatus.Settled,
            ReceiptNumber = "RC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = PaymentDto.From(payment);

        Assert.Equal(payment.Id, dto.Id);
        Assert.Equal(payment.OwnerId, dto.OwnerId);
        Assert.Equal("Settled", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("RC-1", dto.ReceiptNumber);
    }

    [Fact]
    public void AccountDto_MapsTheScarceNumber()
    {
        var account = new Account { Id = Guid.NewGuid(), Code = "S-1", Holder = "v", Currency = "o", Branch = "d", OpeningCents = 10, AvailableCents = 4 };
        var dto = AccountDto.From(account);
        Assert.Equal(10, dto.OpeningCents);
        Assert.Equal(4, dto.AvailableCents);
    }
}
