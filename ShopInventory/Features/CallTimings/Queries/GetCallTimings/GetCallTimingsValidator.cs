using FluentValidation;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

public sealed class GetCallTimingsValidator : AbstractValidator<GetCallTimingsQuery>
{
    public GetCallTimingsValidator()
    {
        RuleFor(query => query.Window)
            .Must(CallTimingWindows.IsKnown)
            .WithMessage($"Window must be one of {string.Join(", ", CallTimingWindows.All)}.");
    }
}
