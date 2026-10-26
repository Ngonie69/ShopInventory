namespace ShopInventory.Features.Email.Commands.ProcessEmailQueue;

/// <summary>
/// What one pass over the email queue did.
/// </summary>
/// <param name="Sent">Delivered to the mail server.</param>
/// <param name="Retrying">Refused or errored, and left Pending for the next pass.</param>
/// <param name="Failed">Refused on its last attempt and given up.</param>
/// <param name="Expired">Not sent within a day of being queued, and dropped unsent.</param>
public sealed record ProcessEmailQueueResult(int Sent, int Retrying, int Failed, int Expired)
{
    public static ProcessEmailQueueResult Nothing { get; } = new(0, 0, 0, 0);
}
