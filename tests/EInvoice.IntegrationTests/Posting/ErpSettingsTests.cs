using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;
using EInvoice.IntegrationTests.Api;
using EInvoice.Web.Posting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>
/// Erp:* settings, validated at startup like every other setting. Posting is off by default (no ERP configured):
/// then approved invoices simply wait in the outbox. A wrong value stops the application, never a later posting.
/// </summary>
public sealed class ErpSettingsTests
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    private static Dictionary<string, string?> Enabled() => new(ApiFactory.Settings(UnusedConnection))
    {
        ["Erp:Enabled"] = "true",
        ["Erp:BaseUrl"] = "http://127.0.0.1:8090/",
        ["Erp:ClientId"] = "einvoice",
        ["Erp:ClientSecret"] = Secret,
    };

    private const string Secret = "settings-test-secret-7f3a";

    [Fact]
    public async Task Without_an_erp_nothing_is_posted_and_the_worker_does_not_run()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient();

        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is PostingWorker);
    }

    [Fact]
    public async Task With_an_erp_the_worker_runs_with_the_default_retry_policy()
    {
        await using var factory = new ApiFactory(Enabled());
        using var client = factory.CreateApiClient();

        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is PostingWorker);
        Assert.Equal(RetryPolicy.Default.MaxAttempts, factory.Services.GetRequiredService<RetryPolicy>().MaxAttempts);
    }

    [Fact]
    public async Task Retry_waits_can_be_configured()
    {
        var settings = Enabled();
        settings["Erp:RetryDelaysSeconds:0"] = "5";
        settings["Erp:RetryDelaysSeconds:1"] = "60";
        await using var factory = new ApiFactory(settings);
        using var client = factory.CreateApiClient();

        var policy = factory.Services.GetRequiredService<RetryPolicy>();

        Assert.Equal((3, TimeSpan.FromSeconds(60)), (policy.MaxAttempts, policy.WaitAfter(2)!.Value));
    }

    [Fact]
    public async Task By_default_four_calls_at_once_and_a_breaker_of_five_failures_and_two_minutes()
    {
        await using var factory = new ApiFactory(Enabled());
        using var client = factory.CreateApiClient();

        Assert.Equal(4, factory.Services.GetRequiredService<PostingLimits>().MaxConcurrentCalls);
        Assert.Equal(BreakerSettings.Default, factory.Services.GetRequiredService<BreakerSettings>());
    }

    [Fact]
    public async Task Concurrency_and_breaker_can_be_configured()
    {
        var settings = Enabled();
        settings["Erp:MaxConcurrentCalls"] = "2";
        settings["Erp:BreakerFailures"] = "3";
        settings["Erp:BreakerOpenSeconds"] = "30";
        await using var factory = new ApiFactory(settings);
        using var client = factory.CreateApiClient();

        Assert.Equal(2, factory.Services.GetRequiredService<PostingLimits>().MaxConcurrentCalls);
        Assert.Equal(new BreakerSettings(3, TimeSpan.FromSeconds(30)), factory.Services.GetRequiredService<BreakerSettings>());
    }

    [Fact]
    public async Task Every_posting_pass_shares_one_breaker()
    {
        // Each pass has its own scope; a breaker per scope would forget every failure after one pass and never open.
        await using var factory = new ApiFactory(Enabled());
        using var client = factory.CreateApiClient();

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<ErpCircuitBreaker>(), second.ServiceProvider.GetRequiredService<ErpCircuitBreaker>());
        Assert.IsType<InvoicePosting>(first.ServiceProvider.GetRequiredService<IInvoicePosting>());
    }

    [Theory]
    [InlineData("Erp:BaseUrl", null)]
    [InlineData("Erp:BaseUrl", "not a url")]
    [InlineData("Erp:BaseUrl", "file:///etc/passwd")]
    [InlineData("Erp:TimeoutSeconds", "0")]
    [InlineData("Erp:TimeoutSeconds", "301")]
    [InlineData("Erp:PollSeconds", "0")]
    [InlineData("Erp:RetryDelaysSeconds:0", "0")]
    [InlineData("Erp:RetryDelaysSeconds:0", "86401")]
    [InlineData("Erp:MaxConcurrentCalls", "0")]
    [InlineData("Erp:MaxConcurrentCalls", "65")]
    [InlineData("Erp:BreakerFailures", "0")]
    [InlineData("Erp:BreakerFailures", "101")]
    [InlineData("Erp:BreakerOpenSeconds", "0")]
    [InlineData("Erp:BreakerOpenSeconds", "3601")]
    [InlineData("Erp:ClientId", null)]
    [InlineData("Erp:ClientId", " ")]
    [InlineData("Erp:TokenUrl", "not a url")]
    [InlineData("Erp:TokenUrl", "ftp://erp.test/token")]
    public async Task A_wrong_setting_stops_the_application_at_startup(string key, string? value)
    {
        var settings = Enabled();
        settings[key] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.IsType<OptionsValidationException>(error);
        Assert.Contains(key.Split(':')[1], error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Posting_on_without_a_client_secret_stops_the_application_with_a_clear_message(string? secret)
    {
        var settings = Enabled();
        settings["Erp:ClientSecret"] = secret;
        await using var factory = new ApiFactory(settings);

        var error = Assert.IsType<OptionsValidationException>(Record.Exception(() => factory.CreateApiClient()));

        Assert.Contains("Erp:ClientSecret is required when Erp:Enabled is true", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posting_off_needs_no_client_secret()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));

        using var client = factory.CreateApiClient(); // starts
    }

    [Fact]
    public async Task A_startup_error_never_repeats_the_secret()
    {
        var settings = Enabled();
        settings["Erp:TokenUrl"] = Secret; // a secret pasted into the wrong setting
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(error);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_url_is_the_erps_oauth_token_unless_configured()
    {
        await using var factory = new ApiFactory(Enabled());
        using var client = factory.CreateApiClient();

        Assert.Equal(new Uri("http://127.0.0.1:8090/oauth/token"), factory.Services.GetRequiredService<ErpCredentials>().TokenUrl);
    }
}
