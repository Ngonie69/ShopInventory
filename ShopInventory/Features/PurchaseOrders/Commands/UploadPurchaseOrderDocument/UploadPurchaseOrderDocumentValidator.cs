using FluentValidation;

namespace ShopInventory.Features.PurchaseOrders.Commands.UploadPurchaseOrderDocument;

public sealed class UploadPurchaseOrderDocumentValidator : AbstractValidator<UploadPurchaseOrderDocumentCommand>
{
    public UploadPurchaseOrderDocumentValidator()
    {
        RuleFor(x => x.PoReferenceNumber)
            .NotEmpty().WithMessage("PO reference number is required")
            .MaximumLength(100).WithMessage("PO reference number must not exceed 100 characters");

        RuleFor(x => x.FileBytes)
            .NotEmpty().WithMessage("File content is required");

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("File name is required")
            .MaximumLength(255).WithMessage("File name must not exceed 255 characters");

        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("Content type is required");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters")
            .When(x => x.Description is not null);
    }
}
