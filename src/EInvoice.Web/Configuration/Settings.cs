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
