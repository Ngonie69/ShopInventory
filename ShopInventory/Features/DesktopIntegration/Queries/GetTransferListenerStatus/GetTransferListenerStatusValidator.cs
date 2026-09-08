using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;

public sealed class GetTransferListenerStatusValidator : AbstractValidator<GetTransferListenerStatusQuery>
{
    public GetTransferListenerStatusValidator()
    {
        // The listener keeps at most twenty recent documents, so anything above that is answered in
        // full anyway; the ceiling is here to reject a nonsense query rather than to cap a real one.
        RuleFor(query => query.RecentDocumentCount)
            .InclusiveBetween(1, 100);
    }
}
