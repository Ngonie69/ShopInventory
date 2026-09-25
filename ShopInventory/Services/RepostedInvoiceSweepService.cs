using MediatR;
using ShopInventory.Features.FiscalisationConfiguration.Commands.FlagRepostedFiscalTransactions;

namespace ShopInventory.Services;

/// <summary>
/// Runs <see cref="FlagRepostedFiscalTransactionsCommand"/> once, shortly after the node starts.
/// </summary>
/// <remarks>
/// A hosted service rather than a step in startup, because it reads SAP: a Service Layer that is slow to
/// answer must not hold the node out of readiness. Delayed for the same reason — a node that has just
/// started is already warming SAP's caches and taking its first requests. Not a Quartz job: it runs once
/// per start, and a clustered job outlives the code that scheduled it.
/// </remarks>
public sealed class RepostedInvoiceSweepService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<RepostedInvoiceSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var scope = serviceScopeFactory.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<ISender>()
                .Send(new FlagRepostedFiscalTransactionsCommand(), stoppingToken);

            if (result.IsError)
            {
                logger.LogWarning(
                    "Flagging reposted invoices in the fiscal transaction log did not finish: {Errors}",
                    string.Join("; ", result.Errors.Select(error => error.Description)));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Flagging reposted invoices in the fiscal transaction log failed");
        }
    }
}
