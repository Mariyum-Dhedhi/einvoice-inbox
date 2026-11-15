using EInvoice.Application.Audit;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EInvoice.Web.Api;

/// <summary>The auditor's check of the hash-chained audit trail.</summary>
[ApiController]
[Route("api/audit")]
public sealed class AuditController : ControllerBase
{
    /// <summary>
    /// Reads the whole trail and checks every link. "isIntact" false names the first broken event ("brokenAt") and the
    /// "problem". Note "count" and "lastHash" outside the system: a chain cannot show that its newest events were cut off.
    /// </summary>
    [HttpGet("verify")]
    [Authorize(Policy = Policies.ReadAudit)]
    [ProducesResponseType<AuditVerification>(StatusCodes.Status200OK)]
    public async Task<AuditVerification> Verify([FromServices] IAuditLog audit, CancellationToken cancellationToken) =>
        await audit.VerifyAsync(cancellationToken).ConfigureAwait(false);
}
