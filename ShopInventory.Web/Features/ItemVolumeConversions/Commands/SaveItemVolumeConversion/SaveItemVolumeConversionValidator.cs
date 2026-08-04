using FluentValidation;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;

public sealed class SaveItemVolumeConversionValidator : AbstractValidator<SaveItemVolumeConversionCommand>
{
    public SaveItemVolumeConversionValidator()
    {
        RuleFor(x => x.ItemCode)
            .NotEmpty()
            .WithMessage("Enter the item code the factor applies to.")
            .MaximumLength(50);

        RuleFor(x => x.ItemName)
            .MaximumLength(200);

        RuleFor(x => x.Notes)
            .MaximumLength(500);

        RuleFor(x => x.VolumeFactor)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Volume factor cannot be negative.")
            .LessThanOrEqualTo(100_000)
            .WithMessage("Volume factor looks wrong. Enter the volume one sold unit represents.");
    }
}
