using FluentValidation;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance.Commands.SetMobileMaintenance;

public sealed class SetMobileMaintenanceValidator : AbstractValidator<SetMobileMaintenanceCommand>
{
    /// <summary>
    /// Long enough for the reason and the expected duration, short enough to read on a phone.
    /// </summary>
    public const int MaxMessageLength = 500;

    public SetMobileMaintenanceValidator()
    {
        RuleFor(x => x.Request.Scope)
            .Must(BeEmptyOrKnownScope)
            .WithMessage("Scope must be either Transactions or All.");

        RuleFor(x => x.Request.Message)
            .MaximumLength(MaxMessageLength)
            .WithMessage($"The maintenance message must be {MaxMessageLength} characters or fewer.");

        RuleForEach(x => x.Request.AppIds)
            .Must(MobileVersionPolicyAppCatalog.IsSupportedPolicyKey)
            .WithMessage("'{PropertyValue}' is not a mobile app this system knows about.");
    }

    private static bool BeEmptyOrKnownScope(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || Enum.TryParse<MobileMaintenanceScope>(value.Trim(), ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed);
}
