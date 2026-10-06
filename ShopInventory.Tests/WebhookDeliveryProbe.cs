using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Tells a test when a fire-and-forget webhook delivery has finished.
/// </summary>
/// <remarks>
/// <c>TriggerEventAsync</c> hands each delivery to <c>Task.Run</c> and returns, so there is no task to
/// await. What there is, is the scope the delivery opens for itself: it is disposed as the delivery's
/// very last act, after the delivery row and the hook's counters are saved, and after the catch that
/// logs a failure. Its disposal is therefore "the delivery is over", whatever the delivery did.
///
/// This replaced polling the database against a five-second deadline. That raced the thread pool —
/// a machine running the rest of the suite can take longer than that to start the delivery — and
/// the polling reads shared the delivery's one SQLite connection while it was still writing on it.
/// Waiting for the signal and then reading once has neither problem.
/// </remarks>
internal sealed class WebhookDeliveryProbe
{
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Registers <see cref="WebhookService"/> so that its deliveries report here.</summary>
    public void AddWebhookService(IServiceCollection services)
        => services.AddScoped<IWebhookService>(provider => new WebhookService(
            provider.GetRequiredService<ApplicationDbContext>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<WebhookService>>(),
            new ReportingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>(), _finished)));

    /// <summary>Waits until the first delivery has finished.</summary>
    /// <remarks>
    /// The timeout only stops a broken build hanging the run. A delivery that is coming finishes in
    /// milliseconds; one that never opens its own scope — the bug these tests pin — fails here.
    /// </remarks>
    public Task WaitForDeliveryAsync() => _finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>The service's scope factory, wrapped so each scope it opens reports its disposal.</summary>
    /// <remarks>
    /// <see cref="WebhookService"/> uses its factory for nothing but deliveries, so every scope seen
    /// here is one. The test's own scopes come from the provider, not through this.
    /// </remarks>
    private sealed class ReportingScopeFactory(IServiceScopeFactory inner, TaskCompletionSource finished)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ReportingScope(inner.CreateScope(), finished);
    }

    private sealed class ReportingScope(IServiceScope inner, TaskCompletionSource finished) : IServiceScope
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            inner.Dispose();
            finished.TrySetResult();
        }
    }
}
