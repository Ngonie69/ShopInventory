using FluentValidation;

namespace ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;

public sealed class UpdateBatchStatusValidator : AbstractValidator<UpdateBatchStatusCommand>
{
    private static readonly string[] SupportedStatuses = ["Released", "Locked", "NotAccessible", "Not Accessible"];

    public UpdateBatchStatusValidator()
    {
        RuleFor(x => x.BatchEntryId)
            .GreaterThan(0).WithMessage("Batch entry id is required");

        RuleFor(x => x.Status)
            .Must(status => SupportedStatuses.Contains(status))
            .WithMessage($"Status must be one of: {string.Join(", ", SupportedStatuses)}");
    }
}