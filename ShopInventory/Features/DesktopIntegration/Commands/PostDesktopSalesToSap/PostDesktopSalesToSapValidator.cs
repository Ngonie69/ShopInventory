using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

public sealed class PostDesktopSalesToSapValidator : AbstractValidator<PostDesktopSalesToSapCommand>
{
    /// <summary>
    /// One console page's worth.
    /// </summary>
    /// <remarks>
    /// A bound rather than a preference. Each sale is its own SAP round trip and they run in
    /// sequence, so an unbounded batch would hold a request open past the Web's five-minute timeout
    /// and keep posting after the operator had been told it failed. Fifty matches the sales list's
    /// page size, which is the largest set a person can actually select, and re-pressing a batch is
    /// safe — the sales that posted replay rather than posting again.
    /// </remarks>
    public const int MaxSalesPerRequest = 50;

    public PostDesktopSalesToSapValidator()
    {
        RuleFor(command => command.CallerUserId)
            .NotEmpty()
            .WithMessage("The post could not be attributed to a signed-in user.");

        RuleFor(command => command.ExternalReferenceIds)
            .NotEmpty()
            .WithMessage("Name at least one sale to post.")
            .Must(references => references.Count <= MaxSalesPerRequest)
            .WithMessage($"Post at most {MaxSalesPerRequest} sales at a time.");

        RuleForEach(command => command.ExternalReferenceIds)
            .NotEmpty()
            .MaximumLength(100);
    }
}
