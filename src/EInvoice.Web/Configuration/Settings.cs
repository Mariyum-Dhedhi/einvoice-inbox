using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Configuration;

/// <summary>ConnectionStrings:EInvoice, from user secrets or an environment variable; never from a file in the repository.</summary>
public sealed class DatabaseSettings
{
    public string? ConnectionString { get; set; }
}

public sealed class IntakeSettings
{
    public const string Section = "Intake";
    public const long Limit = 200L * 1024 * 1024;

    /// <summary>Largest invoice file accepted. Real XRechnung files are kilobytes; the default leaves room for attachments.</summary>
    [Range(1, Limit)]
    public long MaxDocumentBytes { get; set; } = 20L * 1024 * 1024;
}

public sealed class KositSettings
{
    public const string Section = "Kosit";

    /// <summary>The validator service, for example http://127.0.0.1:8081/ (docker compose up -d kosit-validator).</summary>
    public string? BaseUrl { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class MatchingSettings
{
    public const string Section = "Matching";

    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal PriceTolerancePercent { get; set; } = 2.00m;
}

/// <summary>Approval:DefaultLimitEur: the gross amount an approver may approve when their account has no own limit.</summary>
public sealed class ApprovalSettings
{
    public const string Section = "Approval";

    [Range(typeof(decimal), "0", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal DefaultLimitEur { get; set; } = 10_000m;
}

internal sealed class DatabaseSettingsValidator : IValidateOptions<DatabaseSettings>
{
    public ValidateOptionsResult Validate(string? name, DatabaseSettings options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("ConnectionStrings:EInvoice is required (user secrets or the ConnectionStrings__EInvoice environment variable).")
            : ValidateOptionsResult.Success;
}

internal sealed class KositSettingsValidator : IValidateOptions<KositSettings>
{
    public ValidateOptionsResult Validate(string? name, KositSettings options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Kosit:BaseUrl must be an absolute http or https URL.");
}

/// <summary>
/// AI extraction of plain PDF invoices. Off by default: then PDFs are refused and no model is needed.
/// The model runs locally in Ollama; the URL should stay on this machine or the local network.
/// </summary>
public sealed class AiSettings
{
    public const string Section = "Ai";

    public bool Enabled { get; set; }

    /// <summary>For example http://127.0.0.1:11434/.</summary>
    public string? OllamaUrl { get; set; }

    /// <summary>For example qwen2.5:7b; must be pulled in Ollama first.</summary>
    public string? Model { get; set; }

    /// <summary>A long invoice on a small GPU takes a while; a model that has not answered by then counts as unavailable.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

internal sealed class AiSettingsValidator : IValidateOptions<AiSettings>
{
    public ValidateOptionsResult Validate(string? name, AiSettings options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success; // nothing else is read while AI is off
        }

        var failures = new List<string>();
        if (!Uri.TryCreate(options.OllamaUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Ai:OllamaUrl must be an absolute http or https URL when AI is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 100)
        {
            failures.Add("Ai:Model is required when AI is enabled (for example qwen2.5:7b).");
        }

        if (options.TimeoutSeconds is < 1 or > 600)
        {
            failures.Add("Ai:TimeoutSeconds must be between 1 and 600.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
