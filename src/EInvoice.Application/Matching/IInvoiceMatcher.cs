using EInvoice.Domain.Matching;

namespace EInvoice.Application.Matching;

/// <summary>
/// Three-way match of a stored invoice. Implemented by the set-based procedure dbo.usp_MatchInvoice, which applies
/// the same rules as <see cref="ThreeWayMatcher"/> (the two are tested against each other) and stores the result.
/// </summary>
public interface IInvoiceMatcher
{
    Task<InvoiceMatchResult> MatchAsync(Guid invoiceId, MatchingPolicy policy, CancellationToken cancellationToken);
}
