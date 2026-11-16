using EInvoice.Application.Posting;
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
    };

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

    [Theory]
    [InlineData("Erp:BaseUrl", null)]
    [InlineData("Erp:BaseUrl", "not a url")]
    [InlineData("Erp:BaseUrl", "file:///etc/passwd")]
    [InlineData("Erp:TimeoutSeconds", "0")]
    [InlineData("Erp:TimeoutSeconds", "301")]
    [InlineData("Erp:PollSeconds", "0")]
    [InlineData("Erp:RetryDelaysSeconds:0", "0")]
    [InlineData("Erp:RetryDelaysSeconds:0", "86401")]
    public async Task A_wrong_setting_stops_the_application_at_startup(string key, string? value)
    {
        var settings = Enabled();
        settings[key] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.IsType<OptionsValidationException>(error);
        Assert.Contains(key.Split(':')[1], error.Message, StringComparison.Ordinal);
    }
}
