using System.Collections.Concurrent;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;
using EInvoice.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>
/// The client secret and the tokens never leave the token exchange. The whole application runs as in production, its
/// ERP clients wired to the in-memory mock, and every log line of every category is captured at Trace level (the
/// HttpClient factory's request and header logs included) through all four token situations: a fresh token, a renewal
/// after a 401, a second 401, and a token endpoint that is down. Neither the secret nor any token the mock issued may
/// appear in a log line, an exception or a stored reason.
/// </summary>
public sealed class ErpCredentialLeakTests : IDisposable
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    private readonly MockErpFactory _erp = new();
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose()
    {
        _erp.Dispose();
        _logs.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                lines.Enqueue($"{category} scope: {state}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var structured = state is IEnumerable<KeyValuePair<string, object?>> values ? string.Join(";", values.Select(v => $"{v.Key}={v.Value}")) : string.Empty;
                lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {structured} {exception}");
            }
        }
    }

    private ApiFactory App() => new(
        new Dictionary<string, string?>(ApiFactory.Settings(UnusedConnection))
        {
            ["Erp:Enabled"] = "true",
            ["Erp:BaseUrl"] = "http://localhost/",
            ["Erp:ClientId"] = MockErpFactory.ClientId,
            ["Erp:ClientSecret"] = _erp.ClientSecret,
            ["Erp:Scope"] = "api://mock-erp/.default",
        },
        services: s =>
        {
            s.AddHttpClient(HttpErpConnector.ClientName).ConfigurePrimaryHttpMessageHandler(() => _erp.Server.CreateHandler());
            s.AddHttpClient(ErpTokenSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => _erp.Server.CreateHandler());
            s.AddSingleton<ILoggerProvider>(_logs);
            s.AddLogging(logging => logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
        });

    [Fact]
    public async Task Neither_the_secret_nor_any_token_appears_in_logs_errors_or_reasons()
    {
        await using var app = App();
        using var client = app.CreateApiClient();
        using var scope = app.Services.CreateScope();
        var connector = scope.ServiceProvider.GetRequiredService<IErpConnector>();
        var results = new List<ErpResult>();
        Task<ErpResult> PostAsync(string number) => connector.PostAsync(Guid.NewGuid(), ErpContractTests.Sample(number), CancellationToken.None);

        results.Add(await PostAsync("RE-LEAK-1"));                // a fresh token
        await _erp.AuthAsync(expireAll: true);
        results.Add(await PostAsync("RE-LEAK-2"));                // 401, renewed, sent again
        await _erp.AuthAsync(rejectAll: true);
        results.Add(await PostAsync("RE-LEAK-3"));                // 401 twice: refused
        await _erp.AuthAsync(expireAll: true, tokenEndpointDown: true);
        results.Add(await PostAsync("RE-LEAK-4"));                // no token to be had

        Assert.Equal(
            [ErpOutcome.Posted, ErpOutcome.Posted, ErpOutcome.Rejected, ErpOutcome.Transient],
            results.Select(r => r.Outcome));
        var tokens = await _erp.IssuedTokensAsync();
        Assert.True(tokens.Count >= 3, $"{tokens.Count} tokens issued");
        Assert.Contains(_logs.Lines, line => line.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal)); // the capture sees HttpClient's own logs

        var everything = string.Join('\n', _logs.Lines.Concat(results.Select(r => r.Reason ?? string.Empty)));
        Assert.DoesNotContain(_erp.ClientSecret, everything, StringComparison.Ordinal);
        Assert.All(tokens, token => Assert.DoesNotContain(token, everything, StringComparison.Ordinal));
    }
}
