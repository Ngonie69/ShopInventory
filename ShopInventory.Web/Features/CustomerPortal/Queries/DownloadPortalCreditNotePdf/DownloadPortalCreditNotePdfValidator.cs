using FluentValidation;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.DownloadPortalCreditNotePdf;

public sealed class DownloadPortalCreditNotePdfValidator : AbstractValidator<DownloadPortalCreditNotePdfQuery>
{
    public DownloadPortalCreditNotePdfValidator()
    {
        RuleFor(query => query.PortalCardCode).NotEmpty();
        RuleFor(query => query.DocEntry).GreaterThan(0);
        RuleFor(query => query.CardCode).NotEmpty();
    }
}
