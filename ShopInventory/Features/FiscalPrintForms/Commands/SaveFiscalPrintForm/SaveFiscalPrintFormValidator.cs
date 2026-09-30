using FluentValidation;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

public sealed class SaveFiscalPrintFormValidator : AbstractValidator<SaveFiscalPrintFormCommand>
{
    public SaveFiscalPrintFormValidator()
    {
        RuleFor(x => x.CardCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.CardName).MaximumLength(200);

        // Names only: Enum.TryParse would also take "0" and "1", which are the platform's wire values and
        // not something a person chooses between.
        RuleFor(x => x.PrintForm)
            .Must(value => Enum.GetNames<ReceiptPrintForm>()
                .Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("The document type must be Receipt48 or InvoiceA4.");
    }
}
