using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises the SAP credit memos nothing else has filed. See <see cref="SapCreditNoteFiscalisationSweep"/>.
/// </summary>
/// <remarks>
/// <see cref="DisallowConcurrentExecutionAttribute"/> because two passes over one memo would race to file
/// it. The platform would refuse the second, but REVMax is asked before it files, and two passes could
/// both be told "nothing here" a moment apart.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SapCreditNoteFiscalisationJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SapCreditNoteFiscalisationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<SapCreditNoteFiscalisationSweep>();

        try
        {
            await sweep.FiscaliseOutstandingAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The next pass is the retry.
            logger.LogError(ex, "Credit memo fiscalisation sweep failed.");
        }
    }
}
