using System.Text.Json;
using Ledger.Api.Data;
using Ledger.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Modules.Payments;

[ApiController]
[Route("api/payments")]
public class PaymentsController(LedgerDbContext db, PaymentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the payment. The fund hold and the receipt follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<PaymentDto>> Create(CreatePaymentRequest request, CancellationToken ct)
    {
        var payment = await service.CreateAsync(request, ct);
        return Accepted($"/api/payments/{payment.Id}", PaymentDto.From(payment));
    }

    [HttpGet]
    public async Task<IEnumerable<PaymentDto>> List([FromQuery] string? accountCode, CancellationToken ct)
    {
        var query = db.Payments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(accountCode))
            query = query.Where(b => b.AccountCode == accountCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(PaymentDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentDto>> Get(Guid id, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return payment is null ? NotFound() : PaymentDto.From(payment);
    }

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<PaymentDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/payments/{id}", PaymentDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one payment, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
