using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Evening sweep for customers sitting over their credit limit, so credit control can chase them
/// before the next day's orders start getting refused at capture.
/// </summary>
/// <remarks>
/// Runs once after 19:00 CAT — late enough that the day's invoicing and payments are in, and well
/// clear of the 18:00 end-of-day consolidation, so its whole-customer-base SAP sweep competes with
/// nothing a person is waiting on. Silent when nothing is over: an alert that arrives every evening
/// regardless is one nobody reads. The schedule and enablement live on the trigger in
/// <see cref="QuartzConfiguration"/>.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CreditLimitReviewJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CreditLimitSettings _settings;
    private readonly ILogger<CreditLimitReviewJob> _logger;

    public CreditLimitReviewJob(
        IServiceProvider serviceProvider,
        IOptions<CreditLimitSettings> settings,
        ILogger<CreditLimitReviewJob> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ICreditLimitReviewService>();

        CreditLimitReview review;
        try
        {
            review = await reviewService.ReviewAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing to retry against — the next run is tomorrow evening, and the per-order check
            // is the control that actually stops an over-limit order.
            _logger.LogError(ex, "The evening credit limit review failed; no notification was raised");
            return;
        }

        if (review.Breaches.Count == 0)
        {
            _logger.LogInformation(
                "Evening credit limit review: no accounts over their limit ({CustomersRead} customers read, {LimitsMeasured} limits measured)",
                review.CustomersRead,
                review.LimitsMeasured);
            return;
        }

        foreach (var breach in review.Breaches)
        {
            _logger.LogWarning(
                "Over credit limit: {Account}{GroupSuffix} — exposure {Exposure} (balance {Balance}, open orders {OpenOrders}) against a {CreditLimit} limit, {AmountOver} over",
                breach.Describe(),
                breach.IsGroup ? $" [group of {breach.AccountCount}]" : string.Empty,
                breach.Exposure,
                breach.Balance,
                breach.OpenOrders,
                breach.CreditLimit,
                breach.AmountOver);
        }

        _logger.LogWarning(
            "Evening credit limit review: {BreachCount} account(s)/group(s) over their limit by {TotalOver} in total ({CustomersRead} customers read)",
            review.Breaches.Count,
            review.TotalOver,
            review.CustomersRead);

        try
        {
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notificationService.CreateNotificationAsync(
                new CreateNotificationRequest
                {
                    Title = CreditLimitReviewService.BuildSummary(review),
                    Message = CreditLimitReviewService.BuildMessage(review, _settings.ReviewNotificationAccountLimit),
                    Type = "Warning",
                    // Category and ActionUrl together are what put this in front of the people who
                    // capture orders — see NotificationAudienceRules; both filters must pass.
                    Category = "SalesOrder",
                    ActionUrl = "/sales-orders",
                    EntityType = "CreditLimitReview"
                },
                context.CancellationToken);
        }
        catch (Exception ex)
        {
            // The findings are already in the log at Warning; losing the bell is not worth failing
            // the job and having Quartz re-run the whole SAP sweep.
            _logger.LogError(ex, "Failed to raise the notification for the evening credit limit review");
        }
    }
}
