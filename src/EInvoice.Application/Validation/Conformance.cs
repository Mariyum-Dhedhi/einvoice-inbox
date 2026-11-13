namespace EInvoice.Application.Validation;

public enum ConformanceOutcome
{
    Accepted = 1,
    Rejected = 2,

    /// <summary>The check could not be made (service down, timeout, unexpected answer). Never treated as accepted.</summary>
    Unavailable = 3,
}

/// <param name="ErrorCodes">Failed rules (for example BR-CO-15) when rejected; empty otherwise.</param>
public sealed record ConformanceResult(ConformanceOutcome Outcome, IReadOnlyList<string> ErrorCodes)
{
    public static ConformanceResult Accepted { get; } = new(ConformanceOutcome.Accepted, []);

    public static ConformanceResult Unavailable { get; } = new(ConformanceOutcome.Unavailable, []);

    public static ConformanceResult Rejected(IReadOnlyList<string> errorCodes) => new(ConformanceOutcome.Rejected, errorCodes);
}

/// <summary>Checks an XML invoice against EN 16931 and XRechnung. Implemented by the KoSIT validator container.</summary>
public interface IConformanceValidator
{
    Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken);
}
