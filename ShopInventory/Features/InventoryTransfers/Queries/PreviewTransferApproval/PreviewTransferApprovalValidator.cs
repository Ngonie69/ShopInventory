using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Queries.PreviewTransferApproval;

public sealed class PreviewTransferApprovalValidator : AbstractValidator<PreviewTransferApprovalQuery>
{
    public PreviewTransferApprovalValidator()
    {
        RuleFor(query => query.FromWarehouses)
            .NotEmpty().WithMessage("Name at least one source warehouse.")
            .Must(codes => codes.Count <= 20).WithMessage("A transfer can draw from at most 20 warehouses.");
        RuleFor(query => query.ToWarehouses)
            .NotEmpty().WithMessage("Name at least one destination warehouse.")
            .Must(codes => codes.Count <= 20).WithMessage("A transfer can deliver to at most 20 warehouses.");
        RuleForEach(query => query.FromWarehouses).NotEmpty().MaximumLength(50);
        RuleForEach(query => query.ToWarehouses).NotEmpty().MaximumLength(50);
    }
}
