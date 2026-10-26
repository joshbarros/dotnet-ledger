using Ledger.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Receipts;

public record ReceiptDto(Guid PaymentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/receipts")]
[Authorize]
public class ReceiptsController(ReceiptsDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{paymentId:guid}")]
    public async Task<ActionResult<ReceiptDto>> Get(Guid paymentId, CancellationToken ct)
    {
        var doc = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(d => d.PaymentId == paymentId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new ReceiptDto(doc.PaymentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
