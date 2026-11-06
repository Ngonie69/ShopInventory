using FluentValidation;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.GetPortalCreditNotes;

public sealed class GetPortalCreditNotesValidator : AbstractValidator<GetPortalCreditNotesQuery>
{
    /// <summary>The API's own limit: the read is served from SAP, lines included.</summary>
    public const int MaxRangeDays = 366;

    public GetPortalCreditNotesValidator()
    {
        RuleFor(query => query.PortalCardCode).NotEmpty();

        RuleFor(query => query.ToDate).GreaterThanOrEqualTo(query => query.FromDate);

        RuleFor(query => query)
            .Must(query => (query.ToDate.Date - query.FromDate.Date).TotalDays <= MaxRangeDays)
            .WithMessage($"Choose a period of at most {MaxRangeDays} days.");
    }
}
