using FluentValidation;

namespace ShopInventory.Features.StockWriteOffs.Commands.CreateStockWriteOff;

/// <summary>
/// Shape and format only. Whether the warehouse exists, whether the reason is one SAP will accept and
/// whether the stock is actually there are all decisions the handler takes, because each of them needs
/// SAP.
/// </summary>
public sealed class CreateStockWriteOffValidator : AbstractValidator<CreateStockWriteOffCommand>
{
    public CreateStockWriteOffValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();

        RuleFor(command => command.Request.WarehouseCode)
            .NotEmpty().WithMessage("Choose the warehouse the stock is leaving.")
            .MaximumLength(50);

        RuleFor(command => command.Request.Reason)
            .NotEmpty().WithMessage("A write-off needs a reason.")
            .MaximumLength(100);

        RuleFor(command => command.Request.Remarks).MaximumLength(500);
        RuleFor(command => command.Request.ClientRequestId).MaximumLength(100);

        RuleFor(command => command.Request.DocDate)
            .Must(BeEmptyOrIsoDate)
            .WithMessage("Posting date must be a date like 2026-09-20.");

        RuleFor(command => command.Request.Lines)
            .NotEmpty().WithMessage("Add at least one line before writing anything off.");

        RuleForEach(command => command.Request.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.ItemCode)
                .NotEmpty().WithMessage("Every line needs an item code.")
                .MaximumLength(50);

            line.RuleFor(item => item.Quantity)
                .GreaterThan(0).WithMessage("Every line needs a quantity greater than zero.");

            line.RuleFor(item => item.ItemDescription).MaximumLength(200);
            line.RuleFor(item => item.UoMCode).MaximumLength(20);
            line.RuleFor(item => item.BatchNumber).MaximumLength(50);
            line.RuleFor(item => item.SerialNumber).MaximumLength(50);

            // SAP counts a serial number as exactly one unit, so a line naming one cannot be worth
            // more than one. Checked here because it needs nothing but the line itself.
            line.RuleFor(item => item)
                .Must(item => string.IsNullOrWhiteSpace(item.SerialNumber) || item.Quantity == 1)
                .WithMessage("A line naming a serial number is one unit, so its quantity must be 1.");
        });
    }

    private static bool BeEmptyOrIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return DateOnly.TryParseExact(
            value.Trim(),
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);
    }
}
