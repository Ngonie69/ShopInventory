using MediatR;
using Quartz;
using ShopInventory.Features.CustomerDocuments.Commands.DispatchCustomerDocumentDeliveries;

namespace ShopInventory.Services;

/// <summary>
/// Sends customers their documents on WhatsApp, a few at a time, every minute — and whenever someone
/// asks for a send, by trigger.
/// </summary>
/// <remarks>
/// Declared whenever <c>CustomerDocuments:Enabled</c> is on in appsettings.json, regardless of whether
/// the node running it has the gateway configured: the declaration has to be the same on every node,
/// or <c>QuartzStoredJobReconciler</c> on a node without OpenWA would delete it for all of them. A pass
/// on such a node claims nothing and leaves the work to a node that can send. No concurrent run, so one
/// pass at a time across the cluster; that is half of the guard against sending a document twice.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CustomerDocumentDeliveryJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CustomerDocumentDeliveryJob> logger) : IJob
{
    public const string JobName = "customer-document-delivery";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        var sapHeldBack = false;
        if (scope.ServiceProvider.GetService<SapCircuitBreakerState>() is { } circuit
            && circuit.ShouldHoldBackWork(out var holdBackReason))
        {
            sapHeldBack = true;
            logger.LogDebug("Customer documents that need SAP wait: {Reason}.", holdBackReason);
        }

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new DispatchCustomerDocumentDeliveriesCommand(sapHeldBack), context.CancellationToken);

        if (result.IsError)
        {
            logger.LogWarning(
                "Customer document delivery pass failed: {Errors}",
                string.Join("; ", result.Errors.Select(error => error.Description)));
            return;
        }

        var pass = result.Value;
        if (pass.Sent + pass.Deferred + pass.Closed + pass.Uncertain + pass.Reconciled > 0)
        {
            logger.LogInformation(
                "Customer document delivery pass: {Sent} sent, {Deferred} deferred, {Closed} closed, {Uncertain} uncertain, {Reconciled} settled. {Outcome}",
                pass.Sent, pass.Deferred, pass.Closed, pass.Uncertain, pass.Reconciled, pass.Outcome);
        }
    }
}
