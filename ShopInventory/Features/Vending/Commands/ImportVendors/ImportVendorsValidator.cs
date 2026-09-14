using FluentValidation;

namespace ShopInventory.Features.Vending.Commands.ImportVendors;

/// <summary>
/// The shape of an upload only. Whether each row is a vendor that can be added is the handler's call,
/// and it answers row by row rather than refusing the whole request.
/// </summary>
public sealed class ImportVendorsValidator : AbstractValidator<ImportVendorsCommand>
{
    public const int MaxRows = 1000;

    public ImportVendorsValidator()
    {
        RuleFor(command => command.Request).NotNull();

        When(command => command.Request is not null, () =>
        {
            RuleFor(command => command.Request.Rows)
                .NotEmpty().WithMessage("The file has no vendor rows.")
                .Must(rows => rows is null || rows.Count <= MaxRows).WithMessage($"Upload at most {MaxRows} vendors at a time.");

            RuleForEach(command => command.Request.Rows)
                .NotNull();
        });
    }
}
