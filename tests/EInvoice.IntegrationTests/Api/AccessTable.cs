using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Api;

/// <summary>Who may call an endpoint: nobody signed in needed, any signed-in user, or only some roles.</summary>
internal sealed record Access(bool Anonymous, bool AnySignedIn, IReadOnlySet<string> Roles)
{
    public static Access Open { get; } = new(true, true, new HashSet<string>());

    public static Access SignedIn { get; } = new(false, true, new HashSet<string>());

    public static Access Only(params string[] roles) => new(false, false, roles.ToHashSet(StringComparer.Ordinal));

    public bool Allows(string? role) => Anonymous || (role is not null && (AnySignedIn || Roles.Contains(role)));
}

/// <summary>
/// The expected access for every endpoint and page, written down here by hand from the agreed rules, not taken from
/// the code's policies: a wrong policy in the code cannot agree with itself. Role names are spelled out for the same
/// reason. A new endpoint fails the inventory test until it is added here.
/// </summary>
internal static partial class AccessTable
{
    /// <summary>Signed in, but with none of the four roles.</summary>
    public const string NoRole = "(no role)";

    /// <summary>Who calls: the four roles, a signed-in user without a role, and nobody signed in (null).</summary>
    public static IReadOnlyList<string?> Callers { get; } = ["Clerk", "Approver", "Auditor", "Admin", NoRole, null];

    private static readonly Access Readers = Access.Only("Clerk", "Approver", "Auditor");

    public static IReadOnlyDictionary<string, Access> Expected { get; } = new Dictionary<string, Access>(StringComparer.Ordinal)
    {
        // API
        ["GET /api/invoices"] = Readers,
        ["GET /api/invoices/{id:guid}"] = Readers,
        ["GET /api/invoices/{id:guid}/document"] = Readers,
        ["POST /api/invoices"] = Access.Only("Clerk"),
        ["POST /api/invoices/{id:guid}/match"] = Access.Only("Clerk"),
        ["POST /api/invoices/{id:guid}/approve"] = Access.Only("Approver"),
        ["POST /api/invoices/{id:guid}/reject"] = Access.Only("Approver"),
        ["GET /api/audit/verify"] = Access.Only("Auditor"),
        ["POST /account/logout"] = Access.SignedIn,

        // Pages
        ["PAGE /"] = Readers,
        ["PAGE /invoices/{Id:guid}"] = Readers,
        ["PAGE /approvals"] = Access.Only("Approver"),
        ["PAGE /audit"] = Access.Only("Auditor"),
        ["PAGE /admin/users"] = Access.Only("Admin"),
        ["PAGE /account/access-denied"] = Access.SignedIn,
        ["PAGE /account/login"] = Access.Open,
        ["PAGE /Error"] = Access.Open,
        ["PAGE /not-found"] = Access.Open,
    };

    /// <summary>
    /// Every endpoint of the running application as "METHOD /route" (API) or "PAGE /route" (Blazor page).
    /// Left out: static files and Blazor's own endpoints (_blazor, _framework, /{**path:file}), which are not ours.
    /// </summary>
    public static IReadOnlyList<(string Key, Endpoint Endpoint)> Discover(IServiceProvider services)
    {
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var found = new List<(string, Endpoint)>();
        foreach (var endpoint in endpoints)
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            // Not ours: static files, Blazor's connection endpoints and its catch-all that answers 404 for missing files.
            if (route.StartsWith("/_", StringComparison.Ordinal) || FileName().IsMatch(route) || route.Contains(":file}", StringComparison.Ordinal))
            {
                continue;
            }

            if (endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is not null)
            {
                found.Add(($"PAGE {route}", endpoint));
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                found.Add(($"{method} {route}", endpoint));
            }
        }

        return found.DistinctBy(f => f.Item1).OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>The route with its parameter filled in, ready to call.</summary>
    public static string Url(string key) => RouteParameter().Replace(key[(key.IndexOf(' ', StringComparison.Ordinal) + 1)..], Guid.NewGuid().ToString());

    public static bool IsPage(string key) => key.StartsWith("PAGE ", StringComparison.Ordinal);

    public static string Method(string key) => IsPage(key) ? "GET" : key[..key.IndexOf(' ', StringComparison.Ordinal)];

    public static bool DeclaresAccess(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;

    [GeneratedRegex(@"\.[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
