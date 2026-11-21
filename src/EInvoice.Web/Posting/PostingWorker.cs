using EInvoice.Application.Posting;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Posting;

/// <summary>
/// Erp:*. Posting is off unless Erp:Enabled is true; then approved invoices wait in the outbox until an ERP is set.
/// RetryDelaysSeconds are the waits after the 1st, 2nd, ... failed try (one try more than waits), default
/// 30, 120, 600, 1800, 3600 seconds. A busy ERP: at most MaxConcurrentCalls at once (default 4); after BreakerFailures
/// temporary failures in a row (default 5) no calls for BreakerOpenSeconds (default 120), then one trial call.
/// </summary>
public sealed class ErpSettings
{
    public const string Section = "Erp";

    public bool Enabled { get; set; }

    /// <summary>The ERP's base address, for example http://127.0.0.1:8090/ (docker compose up -d mock-erp).</summary>
    public string? BaseUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How often the worker looks for due postings.</summary>
    public int PollSeconds { get; set; } = 10;

    public int[] RetryDelaysSeconds { get; set; } = [];

    public int MaxConcurrentCalls { get; set; } = 4;

    public int BreakerFailures { get; set; } = 5;

    public int BreakerOpenSeconds { get; set; } = 120;

    /// <summary>The configured waits, or the default policy when none are configured.</summary>
    public RetryPolicy Policy() =>
        RetryDelaysSeconds.Length == 0 ? RetryPolicy.Default : new RetryPolicy([.. RetryDelaysSeconds.Select(s => TimeSpan.FromSeconds(s))]);
}

internal sealed class ErpSettingsValidator : IValidateOptions<ErpSettings>
{
    public ValidateOptionsResult Validate(string? name, ErpSettings options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success; // nothing else is read while posting is off
        }

        var failures = new List<string>();
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Erp:BaseUrl must be an absolute http or https URL.");
        }

        if (options.TimeoutSeconds is < 1 or > 300)
        {
            failures.Add("Erp:TimeoutSeconds must be between 1 and 300.");
        }

        if (options.PollSeconds is < 1 or > 3600)
        {
            failures.Add("Erp:PollSeconds must be between 1 and 3600.");
        }

        if (options.RetryDelaysSeconds.Any(s => s is < 1 or > 86_400))
        {
            failures.Add("Erp:RetryDelaysSeconds must each be between 1 and 86400 (one day).");
        }

        if (options.MaxConcurrentCalls is < 1 or > 64)
        {
            failures.Add("Erp:MaxConcurrentCalls must be between 1 and 64.");
        }

        if (options.BreakerFailures is < 1 or > 100)
        {
            failures.Add("Erp:BreakerFailures must be between 1 and 100.");
        }

        if (options.BreakerOpenSeconds is < 1 or > 3600)
        {
            failures.Add("Erp:BreakerOpenSeconds must be between 1 and 3600 (one hour).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Sends due postings to the ERP every few seconds, each pass in its own DI scope (its own DbContext). Only registered
/// when posting is on. A failing pass is logged and the next one tries again; a stuck job's lease runs out.
/// </summary>
public sealed class PostingWorker(IServiceScopeFactory scopes, IOptions<ErpSettings> settings, ILogger<PostingWorker> logger) : BackgroundService
{
    private const int JobsPerPass = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.Value.PollSeconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<IInvoicePosting>().ProcessDueAsync(JobsPerPass, stoppingToken).ConfigureAwait(false);
                if (run.Posted + run.Retried + run.Failed + run.Held > 0)
                {
                    logger.LogInformation("Posting pass: {Posted} posted, {Retried} to retry, {Failed} failed, {Held} held by the breaker.", run.Posted, run.Retried, run.Failed, run.Held);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Posting pass failed; the next pass tries again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Throttling and breaker changes for operators, with the job id as correlation id. Nothing else is passed in, so no
/// invoice content or request body can reach the log.
/// </summary>
public sealed partial class LoggingPostingLog(ILogger<LoggingPostingLog> logger) : IPostingLog
{
    public void Throttled(Guid jobId, TimeSpan wait) => LogThrottled(jobId, (int)wait.TotalSeconds);

    public void BreakerChanged(Guid jobId, BreakerState state) => LogBreaker(jobId, state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ERP throttled posting job {CorrelationId}; next try in {WaitSeconds} s.")]
    private partial void LogThrottled(Guid correlationId, int waitSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ERP circuit breaker is now {State} (posting job {CorrelationId}).")]
    private partial void LogBreaker(Guid correlationId, BreakerState state);
}
