namespace ShopInventory.Web.Models;

/// <summary>
/// The statuses a factory transfer reports, as the API spells them
/// (<c>FactoryTransferStatuses</c> in ShopInventory/Models/Entities/FactoryTransferEntity.cs).
/// </summary>
public static class FactoryTransferStatus
{
    public const string Sending = "Sending";
    public const string SendFailed = "SendFailed";
    public const string InTransit = "InTransit";
    public const string Settling = "Settling";
    public const string SettleFailed = "SettleFailed";
    public const string Accepted = "Accepted";
    public const string PartiallyAccepted = "PartiallyAccepted";
    public const string Rejected = "Rejected";
    public const string Recalled = "Recalled";
    public const string Cancelled = "Cancelled";

    public const string Release = "Release";
    public const string Return = "Return";
}
