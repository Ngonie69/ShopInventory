using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;

public sealed class PostQueuedVanInvoicesValidator : AbstractValidator<PostQueuedVanInvoicesCommand>
{
    /// <summary>
    /// Each sale is its own SAP round trip, in sequence, inside a job that runs every few seconds.
    /// </summary>
    public const int MaxBatchSize = 50;

    public PostQueuedVanInvoicesValidator()
    {
        RuleFor(command => command.BatchSize)
            .InclusiveBetween(1, MaxBatchSize);
    }
}
