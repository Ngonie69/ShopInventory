using MediatR;

namespace ShopInventory.Features.Merchandiser.Events.ProductCatalogChanged;

public sealed record ProductCatalogChangedEvent(
    IReadOnlyList<string> ItemCodes,
    bool IsActive,
    string ChangedBy,
    DateTime ChangedAtUtc) : INotification;