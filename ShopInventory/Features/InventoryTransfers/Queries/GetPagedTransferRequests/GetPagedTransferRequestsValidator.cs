using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

/// <summary>
/// A warehouse filter reaches the Service Layer inside an OData literal, and the SAP client refuses
/// the characters that could end one. Refusing them here answers 400 instead of letting that
/// refusal surface as a failed read.
/// </summary>
public sealed class GetPagedTransferRequestsValidator : AbstractValidator<GetPagedTransferRequestsQuery>
{
    private const int MaxWarehouseCodeLength = 50;

    public GetPagedTransferRequestsValidator()
    {
        RuleFor(query => query.FromWarehouse)
            .MaximumLength(MaxWarehouseCodeLength)
            .Must(BeAPlainCode).WithMessage("From warehouse is not a warehouse code.");

        RuleFor(query => query.ToWarehouse)
            .MaximumLength(MaxWarehouseCodeLength)
            .Must(BeAPlainCode).WithMessage("To warehouse is not a warehouse code.");
    }

    private static bool BeAPlainCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ||
        (!code.Contains('\'') && !code.Contains(';') &&
         !code.Contains("--", StringComparison.Ordinal) && !code.Contains("/*", StringComparison.Ordinal));
}
