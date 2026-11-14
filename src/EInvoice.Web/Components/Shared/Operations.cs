namespace EInvoice.Web.Components.Shared;

/// <summary>
/// Runs one UI operation in its own DI scope. In Blazor Server a scoped service lives as long as the browser
/// connection, so a page that injected the DbContext-backed services directly would share one DbContext across
/// every click (concurrent use fails, tracked data goes stale). Each operation gets fresh services instead.
/// </summary>
public sealed class Operations(IServiceScopeFactory scopes)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TService>()).ConfigureAwait(false);
    }
}
