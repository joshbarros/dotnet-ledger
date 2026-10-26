using Ledger.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Modules.Receipts;

public record ReceiptDto(Guid PaymentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/receipts")]
public class ReceiptsController(LedgerDbContext db) : ControllerBase
{
    [HttpGet("{paymentId:guid}")]
    public async Task<ActionResult<ReceiptDto>> Get(Guid paymentId, CancellationToken ct)
    {
        var doc = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(d => d.PaymentId == paymentId, ct);
        return doc is null
            ? NotFound()
            : new ReceiptDto(doc.PaymentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
