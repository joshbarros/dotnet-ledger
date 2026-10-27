using System.Text.Json;
using Ledger.Shared.Auth;
using Ledger.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Payments;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsController(PaymentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the payment for the signed-in user. Accounts and the receipt follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<PaymentDto>> Create(CreatePaymentRequest request, CancellationToken ct)
    {
        var payment = await service.CreateAsync(request, ct);
        return Accepted($"/api/payments/{payment.Id}", PaymentDto.From(payment));
    }

    /// <summary>Your payments, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<PaymentDto>> List([FromQuery] string? accountCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(accountCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(PaymentDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<PaymentDto> Get(Guid id, CancellationToken ct) => PaymentDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<PaymentDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/payments/{id}", PaymentDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one payment, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
