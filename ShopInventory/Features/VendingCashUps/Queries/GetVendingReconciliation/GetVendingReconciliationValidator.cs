using FluentValidation;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingReconciliation;

public sealed class GetVendingReconciliationValidator : AbstractValidator<GetVendingReconciliationQuery>
{
    /// <summary>A quarter: enough for a month-end look back, short enough to stay one query.</summary>
    public const int MaximumDays = 92;

    public GetVendingReconciliationValidator()
    {
        RuleFor(query => query.ToDate)
            .GreaterThanOrEqualTo(query => query.FromDate.Date)
            .WithMessage("The end of the range is before its start.");
        RuleFor(query => query)
            .Must(query => (query.ToDate.Date - query.FromDate.Date).TotalDays < MaximumDays)
            .WithName("range")
            .WithMessage($"Choose at most {MaximumDays} days.");
        RuleFor(query => query.WarehouseCode).MaximumLength(50);
    }
}
