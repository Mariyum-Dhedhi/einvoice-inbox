using EInvoice.Application.Queries;

namespace EInvoice.Application.Explanation;

public enum ExplanationSource
{
    /// <summary>Built from the reason codes by fixed rules. Always available.</summary>
    Rules = 1,

    /// <summary>Written by a language model from the structured result and checked; a suggestion, not a decision.</summary>
    Ai = 2,
}

/// <summary>Why an invoice is an exception, and a suggested next step. There is deliberately nothing here that could carry a decision.</summary>
/// <param name="FallbackReason">Set when an AI explanation was asked for but not used, saying why; the rule-based text is shown instead.</param>
public sealed record Explanation(string Text, string SuggestedStep, ExplanationSource Source, string? FallbackReason = null);

/// <summary>One problem of the invoice, explained: a finding (by rule code) or a match line (by outcome).</summary>
public sealed record ExplainedIssue(string Code, int? Line, string Text, string Step);

/// <summary>Explains an exception for the person who decides on it. Never changes anything.</summary>
public interface IExceptionExplainer
{
    /// <summary>False when AI is switched off: then the page offers no AI explanation.</summary>
    bool UsesAi { get; }

    Task<Explanation> ExplainAsync(InvoiceDetail invoice, CancellationToken cancellationToken);
}

/// <summary>Used when AI is switched off: the rule-based explanation only.</summary>
public sealed class RuleBasedExceptionExplainer : IExceptionExplainer
{
    public bool UsesAi => false;

    public Task<Explanation> ExplainAsync(InvoiceDetail invoice, CancellationToken cancellationToken) =>
        Task.FromResult(RuleBasedExplainer.Explain(invoice));
}
