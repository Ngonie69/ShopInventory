using FluentValidation;
using ShopInventory.Common.Sales;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

public sealed class CreateDesktopSaleValidator : AbstractValidator<CreateDesktopSaleCommand>
{
    public CreateDesktopSaleValidator()
    {
        // No rule on CardCode or WarehouseCode, here or on the lines. Both are read from the signed-in
        // account by the handler, and a till is expected to send neither — requiring them here would
        // reject exactly the well-formed requests this endpoint now wants. The handler still refuses a
        // request that names a customer or warehouse other than the account's.
        RuleFor(x => x.Request.Lines).NotEmpty().WithMessage("At least one line item is required");

        // The tender stays optional — a till that predates tender capture sends nothing, and the
        // compliance report would rather show that sale as unallocated than assume it was cash. What
        // is not allowed is an unrecognised tender: it would be stored, declared to ZIMRA as cash by
        // the fallback, and posted to the wrong SAP account, all without complaint.
        RuleFor(x => x.Request.PaymentMethod)
            .Must(TenderTypes.IsSupported)
            .When(x => !string.IsNullOrWhiteSpace(x.Request.PaymentMethod))
            .WithMessage($"Payment method must be one of: {string.Join(", ", TenderTypes.SupportedTenders)}");

        // A mobile wallet settles outside the till, so without the customer's confirmation reference
        // there is nothing tying the receipt to money that actually arrived.
        RuleFor(x => x.Request.PaymentReference)
            .NotEmpty()
            .When(x => TenderTypes.RequiresReference(x.Request.PaymentMethod))
            .WithMessage("A payment reference is required for Ecocash and Innbucks");

        RuleForEach(x => x.Request.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemCode).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.DiscountPercent).InclusiveBetween(0, 100);
        });
    }
}
