using FluentValidation;

namespace ShopInventory.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

public sealed class GetLiveTransactionFeedValidator : AbstractValidator<GetLiveTransactionFeedQuery>
{
    public GetLiveTransactionFeedValidator(TimeProvider timeProvider)
    {
        RuleFor(x => x.Limit)
            .InclusiveBetween(1, GetLiveTransactionFeedQuery.MaxLimit)
            .WithMessage($"Limit must be between 1 and {GetLiveTransactionFeedQuery.MaxLimit}.");

        RuleFor(x => x.SinceUtc)
            .Must(since => since is null
                || ToUtc(since.Value) >= timeProvider.GetUtcNow().UtcDateTime - GetLiveTransactionFeedQuery.MaxLookback)
            .WithMessage($"The live feed covers the last {GetLiveTransactionFeedQuery.MaxLookback.TotalDays:0} days; use the reports for anything older.");

        RuleFor(x => x.SinceUtc)
            .Must(since => since is null
                || ToUtc(since.Value) <= timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5))
            .WithMessage("Since cannot be in the future.");
    }

    internal static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
