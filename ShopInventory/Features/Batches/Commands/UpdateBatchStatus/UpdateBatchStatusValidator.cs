using FluentValidation;
using ShopInventory.Common;

namespace ShopInventory.Features.Batches.Commands.UpdateBatchStatus;

public sealed class UpdateBatchStatusValidator : AbstractValidator<UpdateBatchStatusCommand>
{
    public UpdateBatchStatusValidator()
    {
        RuleFor(x => x.BatchEntryId)
            .GreaterThan(0).WithMessage("Batch entry id is required");

        RuleFor(x => x.Status)
            .Must(BatchStatusValues.IsSupported)
            .WithMessage($"Status must be one of: {string.Join(", ", BatchStatusValues.SupportedStatuses)}");
    }
}