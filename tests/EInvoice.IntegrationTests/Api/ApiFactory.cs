using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using EInvoice.Application.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The real web application in memory. Settings go in through UseSetting, exactly like environment variables would,
/// so the same startup validation runs. The KoSIT validator is replaced by a stand-in that accepts everything.
/// Runs as Production unless a test says otherwise: error responses must be safe there.
/// <para>
/// Sign-in: a client from <see cref="CreateApiClient"/> is signed in as a test user through a request header that
/// only this test project knows (<see cref="TestUserHandler"/>). Without the header, requests go through the real
/// sign-in cookie, so anonymous requests and the login page behave exactly as in production.
/// </para>
/// </summary>
internal sealed class ApiFactory(
    IReadOnlyDictionary<string, string?> settings,
    string environment = "Production",
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    public const long MaxDocumentBytes = 64 * 1024;

    /// <summary>The user a test client is signed in as, unless the test names another one: a clerk.</summary>
    public const string DefaultUser = "clerk@test";

    public static readonly Uri BaseAddress = new("https://localhost");

    public static Dictionary<string, string?> Settings(string connectionString) => new()
    {
        ["ConnectionStrings:EInvoice"] = connectionString,
        ["Intake:MaxDocumentBytes"] = MaxDocumentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Kosit:BaseUrl"] = "http://127.0.0.1:9/", // never called: replaced below
        ["Kosit:TimeoutSeconds"] = "5",
        ["Matching:PriceTolerancePercent"] = "2.00",
        ["Approval:DefaultLimitEur"] = "10000.00",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        // In Development the app reads the developer's user secrets (demo password, connection string). A test sees
        // only its own settings, whatever is set on the machine that runs it.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            foreach (var secrets in config.Sources.OfType<JsonConfigurationSource>().Where(s => s.Path == "secrets.json").ToList())
            {
                config.Sources.Remove(secrets);
            }
        });

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IConformanceValidator>();
            s.AddSingleton<IConformanceValidator, AcceptAll>();

            s.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestUserHandler>(TestUserHandler.SchemeName, null)
                .AddPolicyScheme(TestUserHandler.HeaderOrCookie, null, p => p.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(TestUserHandler.UserHeader) ? TestUserHandler.SchemeName : IdentityConstants.ApplicationScheme);
            s.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = TestUserHandler.HeaderOrCookie);

            services?.Invoke(s);
        });
    }

    /// <summary>
    /// A client signed in as <paramref name="user"/> with the given roles; null for an anonymous client with a cookie jar.
    /// The default user is a clerk (upload, read, re-match); any other name has exactly the roles given.
    /// </summary>
    public HttpClient CreateApiClient(string? user = DefaultUser, params string[] roles)
    {
        if (user == DefaultUser && roles.Length == 0)
        {
            roles = ["Clerk"];
        }

        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress, AllowAutoRedirect = false });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestUserHandler.UserHeader, user);
            if (roles.Length > 0)
            {
                client.DefaultRequestHeaders.Add(TestUserHandler.RolesHeader, string.Join(',', roles));
            }
        }

        return client;
    }

    private sealed class AcceptAll : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }
}

/// <summary>Test-only sign-in: the user name (and roles) come from request headers. Exists only in the test project.</summary>
internal sealed class TestUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string HeaderOrCookie = "TestHeaderOrCookie";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string ApprovalLimitHeader = "X-Test-Approval-Limit";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var name = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrEmpty(name))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));
        var limit = Request.Headers[ApprovalLimitHeader].ToString();
        if (limit.Length > 0)
        {
            claims.Add(new Claim(EInvoice.Application.Approval.ApprovalLimits.ClaimType, limit));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal static class ApiHttp
{
    public static MultipartFormDataContent Upload(byte[] content, string fileName = "invoice.xml", string field = "file")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, field, fileName } };
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
