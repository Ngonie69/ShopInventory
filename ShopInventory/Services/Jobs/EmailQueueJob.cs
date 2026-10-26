using MediatR;
using Quartz;
using ShopInventory.Features.Email.Commands.ProcessEmailQueue;

namespace ShopInventory.Services;

/// <summary>
/// Sends what is waiting in the email queue (password resets, and anything sent to
/// <c>POST api/email/queue</c>) every minute.
/// </summary>
/// <remarks>
/// Always declared: whether email is on is read on every pass, so switching it on needs no restart and
/// every node declares the job alike. One pass at a time across the cluster; the per-row claim in
/// <see cref="ProcessEmailQueueHandler"/> covers an on-demand drain running alongside it.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class EmailQueueJob(
    IServiceScopeFactory scopeFactory,
    ILogger<EmailQueueJob> logger) : IJob
{
    public const string JobName = "email-queue";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new ProcessEmailQueueCommand(), context.CancellationToken);

        if (result.IsError)
        {
            logger.LogWarning("Email queue pass failed: {Error}", result.FirstError.Description);
        }
    }
}
