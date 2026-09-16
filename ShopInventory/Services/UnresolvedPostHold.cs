using System.Globalization;

namespace ShopInventory.Services;

/// <summary>
/// How a sale is described while it is held after a post whose outcome is unknown.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the till and van posting services and the sales list, so the time a hold ends is
/// worked out one way. The rule itself — do not send a sale again within the grace window of a post
/// SAP may already hold — lives in the posting services; this only says what it looks like.
/// </para>
/// <para>
/// A hold is not a refusal, and describing it as one sent people into SAP to look for an invoice
/// that was simply not there yet, or not there at all: INV336 (KEF-FAC-20260916-A8CE3BC7D6BE) read
/// "SAP has not accepted this sale" for fifteen minutes and then posted on its own. The notice also
/// replaced the sale's recorded error on every pass in the window, so nobody could see what the post
/// had actually failed with.
/// </para>
/// </remarks>
public static class UnresolvedPostHold
{
    /// <summary>
    /// When a sale whose post went out at <paramref name="postIssuedAtUtc"/> may be sent again.
    /// </summary>
    public static DateTime RetryAfterUtc(DateTime postIssuedAtUtc, int graceMinutes) =>
        DateTime.SpecifyKind(postIssuedAtUtc, DateTimeKind.Utc).AddMinutes(Math.Max(0, graceMinutes));

    /// <summary>Whether a sale is still inside the hold at <paramref name="nowUtc"/>.</summary>
    public static bool IsHeld(DateTime? postIssuedAtUtc, int graceMinutes, DateTime nowUtc) =>
        postIssuedAtUtc is { } issuedAt && nowUtc < RetryAfterUtc(issuedAt, graceMinutes);

    /// <summary>
    /// What a post that ran into the hold reports — the answer a person pressing Post to SAP reads.
    /// </summary>
    public static string Describe(DateTime postIssuedAtUtc, int graceMinutes) =>
        $"A post sent to SAP at {Cat(postIssuedAtUtc)} CAT got no clear answer, so SAP may already hold "
        + "the invoice. It is not sent again until "
        + $"{Cat(RetryAfterUtc(postIssuedAtUtc, graceMinutes))} CAT, when SAP is asked again first.";

    /// <summary>
    /// The error recorded on a held sale that has none — a post whose reply never came back at all,
    /// such as one cut off by a restart.
    /// </summary>
    public static string NoReplyRecorded(string externalReference) =>
        $"No reply to the post was recorded. Check SAP for U_Van_saleorder '{externalReference}'.";

    private static string Cat(DateTime utc) =>
        AuditService.ToCAT(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
}
