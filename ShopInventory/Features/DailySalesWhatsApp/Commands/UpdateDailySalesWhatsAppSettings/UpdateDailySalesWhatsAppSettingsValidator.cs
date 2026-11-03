using FluentValidation;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

public sealed class UpdateDailySalesWhatsAppSettingsValidator : AbstractValidator<UpdateDailySalesWhatsAppSettingsCommand>
{
    public const int MaxRecipients = 10;
    public const int MaxRows = 40;
    public const int MaxLabelLength = 60;
    public const int MaxCodesPerRow = 10;
    public const int MaxCodeLength = 50;
    public const int MaxHeadingLength = 80;

    public UpdateDailySalesWhatsAppSettingsValidator()
    {
        RuleFor(x => x.Recipients)
            .NotNull()
            .Must(list => list.Count(number => !string.IsNullOrWhiteSpace(number)) <= MaxRecipients)
            .WithMessage($"At most {MaxRecipients} numbers can receive the report.");

        RuleFor(x => x.Recipients)
            .Must(list => list.Any(number => !string.IsNullOrWhiteSpace(number)))
            .When(x => x.Enabled)
            .WithMessage("Name at least one WhatsApp number before switching the report on.");

        RuleFor(x => x.ShopWarehouseCode).MaximumLength(MaxCodeLength);
        RuleFor(x => x.ShopHeading).MaximumLength(MaxHeadingLength);

        RuleFor(x => x.VanRows).NotNull().Must(rows => rows.Count <= MaxRows)
            .WithMessage($"The van table can have at most {MaxRows} rows.");
        RuleForEach(x => x.VanRows).SetValidator(new RowValidator("route"));

        RuleFor(x => x.ProductRows).NotNull().Must(rows => rows.Count <= MaxRows)
            .WithMessage($"The shop table can have at most {MaxRows} rows.");
        RuleForEach(x => x.ProductRows).SetValidator(new RowValidator("item"));
    }

    private sealed class RowValidator : AbstractValidator<DailySalesWhatsAppRow>
    {
        public RowValidator(string codeName)
        {
            RuleFor(row => row.Label)
                .NotEmpty().WithMessage("Every row needs a label.")
                .MaximumLength(MaxLabelLength);

            RuleFor(row => row.Codes)
                .NotNull()
                .Must(codes => codes.Any(code => !string.IsNullOrWhiteSpace(code)))
                .WithMessage(row => $"'{row.Label}' needs at least one {codeName} code.")
                .Must(codes => codes.Count <= MaxCodesPerRow)
                .WithMessage(row => $"'{row.Label}' can add up at most {MaxCodesPerRow} {codeName} codes.");

            RuleForEach(row => row.Codes).MaximumLength(MaxCodeLength);
        }
    }
}
