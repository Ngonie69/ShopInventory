using FluentValidation;
using ShopInventory.Common.Sales;

namespace ShopInventory.Features.VendingCashUps.Commands.SaveVendingCashUp;

public sealed class SaveVendingCashUpValidator : AbstractValidator<SaveVendingCashUpCommand>
{
    /// <summary>Far more than any depot takes in a day, and low enough to catch a slipped digit.</summary>
    public const decimal MaximumAmount = 10_000_000m;

    public const int MaximumReasonLength = 500;
    public const int MaximumNoteLength = 1000;

    public static readonly string[] CountableTenders = [TenderTypes.Swipe, TenderTypes.Ecocash, TenderTypes.Innbucks];

    public SaveVendingCashUpValidator()
    {
        RuleFor(command => command.CashierUserId).NotEmpty();
        RuleFor(command => command.WarehouseCode).NotEmpty().MaximumLength(50);
        RuleFor(command => command.ExpectedRevision).GreaterThanOrEqualTo(0);
        RuleFor(command => command.VarianceReason).MaximumLength(MaximumReasonLength);
        RuleFor(command => command.Note).MaximumLength(MaximumNoteLength);

        RuleFor(command => command.Currencies)
            .NotEmpty().WithMessage("Count at least one currency.")
            .Must(lines => lines.Select(line => VendingCashUpMath.NormalizeCurrency(line.Currency)).Distinct().Count() == lines.Count)
            .WithMessage("Each currency can be counted once.");

        RuleForEach(command => command.Currencies).ChildRules(line =>
        {
            line.RuleFor(currency => currency.Currency).NotEmpty().MaximumLength(10);
            line.RuleFor(currency => currency.OpeningFloat).InclusiveBetween(0m, MaximumAmount);
            line.RuleFor(currency => currency.CountedCash!.Value).InclusiveBetween(0m, MaximumAmount)
                .When(currency => currency.CountedCash.HasValue);
            line.RuleFor(currency => currency.Banked).InclusiveBetween(0m, MaximumAmount);
            line.RuleFor(currency => currency.DepositReference).MaximumLength(100);
            line.RuleFor(currency => currency.Denominations)
                .Must(notes => notes is null || notes.Select(note => note.Value).Distinct().Count() == notes.Count)
                .WithMessage("Each note or coin can be listed once per currency.");
            line.RuleForEach(currency => currency.Denominations).ChildRules(note =>
            {
                note.RuleFor(denomination => denomination.Value).GreaterThan(0m).LessThanOrEqualTo(1000m);
                note.RuleFor(denomination => denomination.Quantity).InclusiveBetween(0, 1_000_000);
            });
        });

        RuleFor(command => command.Tenders)
            .Must(lines => lines
                .Select(line => (VendingCashUpMath.NormalizeCurrency(line.Currency), TenderName(line.Tender)))
                .Distinct().Count() == lines.Count)
            .WithMessage("Each tender can be declared once per currency.");

        RuleForEach(command => command.Tenders).ChildRules(line =>
        {
            line.RuleFor(tender => tender.Currency).NotEmpty().MaximumLength(10);
            line.RuleFor(tender => tender.Tender)
                .Must(tender => CountableTenders.Contains(TenderName(tender)))
                .WithMessage("A non-cash tender is Swipe, Ecocash or Innbucks. Cash is counted, not declared.");
            line.RuleFor(tender => tender.Declared).InclusiveBetween(0m, MaximumAmount);
        });
    }

    public static string TenderName(string? tender) =>
        TenderTypes.TryNormalize(tender, out var normalized) ? normalized : tender?.Trim() ?? string.Empty;
}
