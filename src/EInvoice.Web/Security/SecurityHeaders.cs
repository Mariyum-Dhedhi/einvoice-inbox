namespace EInvoice.Web.Security;

/// <summary>
/// What the browser is told on every response (SecurityHeaderTests; found missing by the ZAP scan, tests/zap).
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// Content only from this site; no plug-ins; framed by nobody; forms post only here; no base-URL tricks.
    /// Styles allow inline: MudBlazor positions and colours components with style attributes. Scripts do not.
    /// connect-src 'self' covers the Blazor WebSocket (same origin).
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Features this app never uses, switched off for it and anything it might load.</summary>
    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), clipboard-read=(), fullscreen=()";

    public static void Apply(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer"; // invoice ids in addresses stay on this site
        headers["Permissions-Policy"] = PermissionsPolicy;
        // Isolated from other sites' windows and documents: nobody else may open, read or embed what this app serves.
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    }
}
