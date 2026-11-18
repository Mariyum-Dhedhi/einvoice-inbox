using System.Net;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Configuration;

/// <summary>
/// Tunnel:Enabled: the app runs behind Tailscale Funnel in the same network namespace (compose profile "public",
/// docs/self-hosting.md). The tunnel ends HTTPS and forwards plain HTTP from 127.0.0.1, adding X-Forwarded-For and
/// X-Forwarded-Proto. Only then, and only from loopback, are these headers believed.
/// </summary>
public sealed class TunnelSettings
{
    public const string Section = "Tunnel";

    public bool Enabled { get; set; }
}

/// <summary>
/// DataProtection:KeysPath: an absolute folder for the keys that protect sign-in cookies and form tokens. In a container
/// it is a mounted volume, so a restart or an update does not sign everyone out. Unset: ASP.NET Core's default place.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string Section = "DataProtection";

    public string? KeysPath { get; set; }
}

internal sealed class DataProtectionSettingsValidator : IValidateOptions<DataProtectionSettings>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionSettings options) =>
        options.KeysPath is { Length: > 0 } path && !Path.IsPathFullyQualified(path)
            ? ValidateOptionsResult.Fail("DataProtection:KeysPath must be an absolute folder (in the container: the mounted volume).")
            : ValidateOptionsResult.Success;
}

internal static class Hosting
{
    public static IServiceCollection AddEInvoiceHosting(this IServiceCollection services)
    {
        services.AddOptions<TunnelSettings>().BindConfiguration(TunnelSettings.Section).ValidateOnStart();

        // Believe the client's address and scheme from the tunnel only: loopback, one hop (the entry the tunnel added
        // last; a visitor's own X-Forwarded-For comes before it and is ignored).
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
            o.ForwardLimit = 1;
        });

        services.AddOptions<DataProtectionSettings>().BindConfiguration(DataProtectionSettings.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionSettings>, DataProtectionSettingsValidator>();
        services.AddDataProtection();
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<DataProtectionSettings>, ILoggerFactory>((keys, settings, logging) =>
        {
            if (settings.Value.KeysPath is { Length: > 0 } path && Path.IsPathFullyQualified(path))
            {
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), logging);
            }
        });
        return services;
    }

    /// <summary>First in the pipeline, so everything after it (HTTPS, HSTS, the address limit) sees the visitor.</summary>
    public static void UseTunnel(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<TunnelSettings>>().Value.Enabled)
        {
            app.UseForwardedHeaders();
        }
    }
}
