using FluentValidation;

namespace ShopInventory.Features.Timesheets.Commands.CheckIn;

public sealed class CheckInValidator : AbstractValidator<CheckInCommand>
{
    public CheckInValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .MaximumLength(50).WithMessage("Username must not exceed 50 characters.");

        RuleFor(x => x.CustomerCode)
            .NotEmpty().WithMessage("Customer code is required.")
            .MaximumLength(50).WithMessage("Customer code must not exceed 50 characters.");

        RuleFor(x => x.CustomerName)
            .NotEmpty().WithMessage("Customer name is required.")
            .MaximumLength(200).WithMessage("Customer name must not exceed 200 characters.");

        // Coordinates stay required, because a check-in with no position proves nothing and this is a
        // compliance control. The one exception is a caller that has said why it has none: a handset
        // indoors with GPS switched off has genuinely arrived, and refusing the visit does not produce
        // a better record — it produces no record, and a call the rep really made goes uncounted.
        //
        // The reason is what makes the exception safe to allow. It is stored on the entry, so a
        // position-less visit is visible as exactly that rather than blending in.
        RuleFor(x => x.Latitude)
            .NotNull()
            .When(x => !HasStatedLocationUnavailable(x))
            .WithMessage("GPS coordinates are required for check-in.");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue)
            .WithMessage("Latitude must be between -90 and 90.");

        RuleFor(x => x.Longitude)
            .NotNull()
            .When(x => !HasStatedLocationUnavailable(x))
            .WithMessage("GPS coordinates are required for check-in.");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue)
            .WithMessage("Longitude must be between -180 and 180.");

        RuleFor(x => x.Notes)
            .MaximumLength(500).When(x => x.Notes is not null)
            .WithMessage("Notes must not exceed 500 characters.");

        RuleFor(x => x.Capture!.ClientReference)
            .MaximumLength(100).When(x => x.Capture?.ClientReference is not null)
            .WithMessage("Client reference must not exceed 100 characters.");
    }

    /// <summary>
    /// Whether the caller has explicitly reported that no fix was obtainable, rather than simply
    /// omitting the coordinates. Both a stated reason and a source of <c>None</c> count — an older
    /// handset sends neither, so it stays under the original rule.
    /// </summary>
    private static bool HasStatedLocationUnavailable(CheckInCommand command)
    {
        var capture = command.Capture;
        if (capture is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(capture.LocationUnavailableReason)
               || string.Equals(
                   capture.LocationSource,
                   Models.Entities.TimesheetLocationSources.None,
                   StringComparison.OrdinalIgnoreCase);
    }
}
