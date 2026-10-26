namespace ShopInventory.Features.Email;

/// <summary>
/// The states of an <see cref="Models.EmailQueueItem"/>. Pending is waiting (first try or a retry),
/// Sending is claimed by a pass, and Sent and Failed are final.
/// </summary>
public static class EmailQueueStatus
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}
