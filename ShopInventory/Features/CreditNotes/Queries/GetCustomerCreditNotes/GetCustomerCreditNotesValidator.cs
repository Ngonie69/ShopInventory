using FluentValidation;

namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

public sealed class GetCustomerCreditNotesValidator : AbstractValidator<GetCustomerCreditNotesQuery>
{
    /// <summary>
    /// A year. The read is served from SAP with every memo's lines, so the range is what bounds it.
    /// </summary>
    public const int MaxRangeDays = 366;

    /// <summary>A multi-account customer's main account and its sub accounts; far more than any has.</summary>
    public const int MaxAccounts = 50;

    public GetCustomerCreditNotesValidator()
    {
        RuleFor(query => query.CardCodes)
            .NotEmpty()
            .WithMessage("Name at least one customer account.")
            .Must(cardCodes => cardCodes.Count <= MaxAccounts)
            .WithMessage($"Name at most {MaxAccounts} customer accounts.");

        RuleForEach(query => query.CardCodes)
            .NotEmpty()
            .WithMessage("A customer account code cannot be blank.")
            .MaximumLength(50);

        RuleFor(query => query.ToDate)
            .GreaterThanOrEqualTo(query => query.FromDate)
            .WithMessage("The period must end on or after the day it starts.");

        RuleFor(query => query)
            .Must(query => (query.ToDate.Date - query.FromDate.Date).TotalDays <= MaxRangeDays)
            .WithMessage($"Choose a period of at most {MaxRangeDays} days.");
    }
}
