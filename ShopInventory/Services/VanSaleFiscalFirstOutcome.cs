using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// What <see cref="VanSaleFiscalFirstPoster"/> did with one sale.
/// </summary>
/// <param name="Status">How far the sale got. See <see cref="VanSaleFiscalFirstStatus"/>.</param>
/// <param name="Sale">
/// The row carrying the receipt, when one was written. It holds the verification code, QR, fiscal day and
/// receipt number the handset prints, and it is what a credit note reads the receipt number back from.
/// </param>
/// <param name="SapDocEntry">The invoice SAP created, when it did.</param>
/// <param name="SapDocNum">The invoice number SAP gave it, when it did.</param>
/// <param name="Error">Why the sale stopped short of <see cref="VanSaleFiscalFirstStatus.Posted"/>.</param>
/// <param name="Transient">
/// Whether the failure looks like SAP or the device being unavailable, rather than refusing this sale.
/// </param>
/// <param name="Adopted">
/// With <see cref="VanSaleFiscalFirstStatus.Posted"/>: SAP already held the invoice, so it was adopted
/// rather than raised again. A person who pressed Post is told which of the two happened.
/// </param>
/// <param name="Deferred">
/// With <see cref="VanSaleFiscalFirstStatus.AwaitingSap"/>: SAP was not asked, because the caller said
/// not to (<see cref="VanSaleFiscalFirstRequest.PostToSapNow"/>) — not because it refused. Nothing is wrong
/// with the sale; the queue posts it.
/// </param>
public sealed record VanSaleFiscalFirstOutcome(
    VanSaleFiscalFirstStatus Status,
    DesktopSaleEntity? Sale,
    int? SapDocEntry = null,
    int? SapDocNum = null,
    string? Error = null,
    bool Transient = false,
    bool Adopted = false,
    bool Deferred = false)
{
    /// <summary>Whether a fiscal receipt exists for this sale, so it can no longer be refused.</summary>
    public bool IsFiscalised =>
        Status is VanSaleFiscalFirstStatus.Posted or VanSaleFiscalFirstStatus.AwaitingSap;
}
