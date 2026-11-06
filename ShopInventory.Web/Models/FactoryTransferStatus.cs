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
    public const string Delivery = "Delivery";
    public const string DepotReturn = "DepotReturn";

    /// <summary>Nothing more will happen to it: decided and posted, or abandoned before SAP.</summary>
    public static bool IsSettled(string status) => status is Accepted or PartiallyAccepted
        or Rejected or Recalled or Cancelled;

    /// <summary>The page's word for a status.</summary>
    public static string Describe(string status) => status switch
    {
        Sending => "Sending",
        SendFailed => "Send failed",
        InTransit => "In transit",
        Settling => "Posting",
        SettleFailed => "Post failed",
        Accepted => "Accepted",
        PartiallyAccepted => "Part-accepted",
        Rejected => "Rejected",
        Recalled => "Recalled",
        Cancelled => "Cancelled",
        _ => status
    };

    public static string DirectionWord(string direction) => direction switch
    {
        Return or DepotReturn => "return",
        Delivery => "delivery",
        _ => "release"
    };
}
