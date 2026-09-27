using FluentValidation;

namespace ShopInventory.Features.CountVariance.Queries.GetCountingDocuments;

public sealed class GetCountingDocumentsValidator : AbstractValidator<GetCountingDocumentsQuery>
{
    private static readonly string[] Statuses = ["open", "closed", "all"];

    public GetCountingDocumentsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => string.IsNullOrWhiteSpace(status)
                            || Statuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("Status must be open, closed or all.");

        RuleFor(query => query.Search).MaximumLength(100);
    }
}
