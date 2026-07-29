using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using ShopInventory.Features.Prices.Commands.SyncPriceCatalog;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that syncs the SAP price catalog. Enablement (SAP:AutoSyncEnabled), interval
/// (SAP:SyncIntervalHours) and initial delay (SAP:InitialDelayMinutes) are applied when the
/// trigger is registered in QuartzConfiguration; the job itself performs one sync per fire.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PriceCatalogSyncJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PriceCatalogSyncJob> _logger;

    public PriceCatalogSyncJob(
        IServiceScopeFactory scopeFactory,
        ILogger<PriceCatalogSyncJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;

        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SyncPriceCatalogCommand(), cancellationToken);
        if (result.IsError)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        _logger.LogInformation("Price catalog sync completed");
    }
}
