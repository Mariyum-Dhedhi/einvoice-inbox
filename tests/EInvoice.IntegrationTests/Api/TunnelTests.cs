using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The public demo runs behind Tailscale Funnel, which shares the app's network: the tunnel ends HTTPS and forwards
/// plain HTTP from 127.0.0.1 with X-Forwarded-For and X-Forwarded-Proto (Tunnel:Enabled). Those headers are believed
/// only then, and only from loopback, so nobody else can claim to be HTTPS or another address; of the forwarded
/// addresses only the last one counts (the one the tunnel added), so a visitor cannot choose their own.
/// No database: the demo sign-in refuses a request without a form token before it touches one, and the address limit
/// counts it all the same.
/// </summary>
public sealed class TunnelTests
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";
    private const string Hsts = "Strict-Transport-Security";

    /// <summary>Test-only: the connection's remote address, as the network would give it (TestServer gives none).</summary>
    internal const string RemoteHeader = "X-Test-Remote-Address";

    private static ApiFactory Factory(bool tunnel, int signInsPerHour = 1000)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Demo:Enabled"] = "true";
        settings["Demo:SignInsPerHourPerAddress"] = signInsPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (tunnel)
        {
            settings["Tunnel:Enabled"] = "true";
        }

        return new ApiFactory(settings, services: s => s.AddSingleton<IStartupFilter, RemoteAddressFromHeader>());
    }

    /// <summary>Plain HTTP, as the tunnel talks to the app.</summary>
    private static HttpClient Client(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string remote, string? proto = null, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/login");
        Headers(request, remote, proto, forwardedFor);
        return await client.SendAsync(request);
    }

    /// <summary>A demo click without a form token: 400 from the endpoint, or 429 when the address has used up its limit.</summary>
    private static async Task<HttpStatusCode> ClickAsync(HttpClient client, string remote, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/demo") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = "Clerk" }) };
        Headers(request, remote, "https", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static void Headers(HttpRequestMessage request, string remote, string? proto, string? forwardedFor)
    {
        request.Headers.Add(RemoteHeader, remote);
        if (proto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", proto);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
    }

    [Fact]
    public async Task Behind_the_tunnel_a_request_it_forwards_as_https_is_treated_as_https()
    {
        await using var factory = Factory(tunnel: true);
        using var client = Client(factory);

        using var response = await GetAsync(client, "127.0.0.1", proto: "https", forwardedFor: "203.0.113.10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(Hsts), "HSTS is sent on HTTPS only: the forwarded scheme was not believed");
    }

    [Theory]
    [InlineData("172.18.0.5")]  // another container on the Docker network
    [InlineData("192.168.1.20")] // a machine on the home network
    public async Task Forwarded_headers_from_anywhere_but_the_tunnel_are_ignored(string remote)
    {
        await using var factory = Factory(tunnel: true);
        using var client = Client(factory);

        using var response = await GetAsync(client, remote, proto: "https", forwardedFor: "203.0.113.10");

        Assert.False(response.Headers.Contains(Hsts));
    }

    [Fact]
    public async Task Without_the_tunnel_setting_forwarded_headers_are_ignored_even_from_loopback()
    {
        await using var factory = Factory(tunnel: false);
        using var client = Client(factory);

        using var response = await GetAsync(client, "127.0.0.1", proto: "https", forwardedFor: "203.0.113.10");

        Assert.False(response.Headers.Contains(Hsts));
    }

    [Fact]
    public async Task Behind_the_tunnel_each_visitor_address_has_its_own_sign_in_limit()
    {
        await using var factory = Factory(tunnel: true, signInsPerHour: 1);
        using var client = Client(factory);

        Assert.Equal(HttpStatusCode.BadRequest, await ClickAsync(client, "127.0.0.1", "203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ClickAsync(client, "127.0.0.1", "203.0.113.10"));
        Assert.Equal(HttpStatusCode.BadRequest, await ClickAsync(client, "127.0.0.1", "203.0.113.11")); // someone else
    }

    [Fact]
    public async Task A_visitor_cannot_choose_their_address_by_sending_their_own_forwarded_header()
    {
        // The tunnel appends the real address to whatever the visitor sent: only that last entry counts.
        await using var factory = Factory(tunnel: true, signInsPerHour: 1);
        using var client = Client(factory);
        await ClickAsync(client, "127.0.0.1", "203.0.113.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, await ClickAsync(client, "127.0.0.1", "198.51.100.1, 203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ClickAsync(client, "127.0.0.1", "198.51.100.2, 203.0.113.10"));
    }

    [Fact]
    public async Task Forwarded_addresses_from_elsewhere_do_not_split_the_limit()
    {
        await using var factory = Factory(tunnel: true, signInsPerHour: 1);
        using var client = Client(factory);
        await ClickAsync(client, "172.18.0.5", "203.0.113.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, await ClickAsync(client, "172.18.0.5", "203.0.113.11"));
    }

    /// <summary>Sets the connection's remote address from a test header, before anything else in the pipeline runs.</summary>
    private sealed class RemoteAddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[RemoteHeader].ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
