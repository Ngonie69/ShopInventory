using FluentValidation;

namespace ShopInventory.Features.Timesheets.Commands.CheckOut;

public sealed class CheckOutValidator : AbstractValidator<CheckOutCommand>
{
    public CheckOutValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .MaximumLength(50).WithMessage("Username must not exceed 50 characters.");

        RuleFor(x => x.Latitude)
            .NotNull().WithMessage("GPS coordinates are required for check-out.")
            .InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue)
            .WithMessage("Latitude must be between -90 and 90.");

        RuleFor(x => x.Longitude)
            .NotNull().WithMessage("GPS coordinates are required for check-out.")
            .InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue)
            .WithMessage("Longitude must be between -180 and 180.");

        RuleFor(x => x.Notes)
            .MaximumLength(500).When(x => x.Notes is not null)
            .WithMessage("Notes must not exceed 500 characters.");
    }
}
