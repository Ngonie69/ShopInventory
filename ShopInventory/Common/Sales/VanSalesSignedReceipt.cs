using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The ZIMRA receipt a van handset signed for itself, as reported by whichever endpoint carried the sale.
/// </summary>
/// <remarks>
/// There is one signing routine on the handset and it does not know which endpoint its result is bound
/// for: given the same cart, the canonical payload, the hash and the signature are byte-identical whether
/// the sale went up as an offline batch or as an online direct invoice. This interface is that fact
/// expressed in the API — the two request DTOs carry the same field names, the same JSON names and the
/// same completeness rules, so a handset never has two receipt formats to keep in step.
///
/// <para>
/// Every value here is <b>reported, never requested</b>. The receipt was stamped before the request was
/// made and the customer is holding the printout; the fiscalisation platform re-derives the signed payload
/// from what is forwarded and refuses anything that does not hash to the signature. Nothing may be
/// rounded, re-derived or defaulted on the way through.
/// </para>
/// </remarks>
public interface IVanSalesSignedReceipt
{
    /// <summary>The lease's device id, as text. Parsed to <see cref="DesktopSaleEntity.FiscalDeviceId"/>.</summary>
    string? FiscalDeviceId { get; }

    int? FiscalDayNo { get; }

    int? ReceiptGlobalNo { get; }

    int? ReceiptCounter { get; }

    string? VerificationCode { get; }

    string? QrCode { get; }

    /// <summary>
    /// The instant the receipt was signed at, in the van's local wall clock at second precision. Part of
    /// the signed payload: a one-second difference invalidates the signature.
    /// </summary>
    DateTime? ReceiptDate { get; }

    /// <summary>
    /// When the handset opened the fiscal day it signed this receipt into. The platform never opened that
    /// day itself, so it has no other way to learn it.
    /// </summary>
    DateTime? FiscalDayOpenedAt { get; }

    /// <summary>
    /// The hash this receipt was chained onto, or null for the first receipt of a fiscal day. Sent
    /// explicitly rather than inferred from the receipt before it, so a divergence is reported as the
    /// chain break it is instead of surfacing as an unexplained signature failure.
    /// </summary>
    string? PreviousReceiptHash { get; }

    /// <summary>Base64 SHA-256 of the canonical payload the handset signed.</summary>
    string? DeviceSignatureHash { get; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that same payload.</summary>
    string? DeviceSignatureValue { get; }
}

/// <summary>
/// The rules that decide what a reported receipt means, and the one place a sale row is stamped with it.
/// </summary>
/// <remarks>
/// Shared by both van endpoints deliberately. The three-way judgement below — signed, stamped but
/// unsubmittable, never stamped — is what decides whether a whole handset stops trading, and two copies of
/// it would eventually disagree about a sale that arrived by one route rather than the other. The
/// consequences of the two paths differ, and that difference is expressed by which
/// <c>SourceSystem</c> and posting fields the caller sets on the row — not by re-deciding the fiscal
/// question.
/// </remarks>
public static class VanSalesSignedReceipt
{
    /// <summary>Whether this sale carries everything the platform needs to accept the receipt.</summary>
    public static bool HasSignedReceipt(this IVanSalesSignedReceipt receipt) =>
        !string.IsNullOrWhiteSpace(receipt.DeviceSignatureHash) &&
        !string.IsNullOrWhiteSpace(receipt.DeviceSignatureValue) &&
        receipt.ReceiptDate.HasValue &&
        receipt.ReceiptDate.Value != default &&
        receipt.FiscalDayNo is > 0 &&
        receipt.ReceiptCounter is > 0 &&
        receipt.ReceiptGlobalNo is > 0;

    /// <summary>
    /// Whether the handset took a number off its device's chain for this sale.
    /// </summary>
    /// <remarks>
    /// The difference between a sale that is missing something and a sale that was never stamped, and it
    /// decides whether the whole device stops.
    ///
    /// A number was consumed, so a receipt was printed and handed over, but something needed to submit it
    /// is missing: that is a hole in the chain. The platform will not accept the receipt that follows it,
    /// so the device has to stop until a person reconciles it.
    ///
    /// No number was consumed — an older handset that cannot stamp at all: nothing is missing from the
    /// chain, nothing is waiting behind it, and stopping the device would punish the wrong sale.
    ///
    /// It is also the test for "did this handset stamp for itself", which is what decides whether the
    /// server may fiscalise the sale. A handset that owns a device must be the only writer on its chain.
    /// </remarks>
    public static bool ClaimsReceiptSequence(this IVanSalesSignedReceipt receipt) =>
        receipt.ReceiptGlobalNo is > 0 || receipt.ReceiptCounter is > 0;

    /// <summary>
    /// Stamps a sale row with the receipt exactly as it was signed, and with what is to become of it.
    /// </summary>
    /// <param name="sale">The row being built. Only the fiscal columns are touched.</param>
    /// <param name="receipt">What the handset reported.</param>
    /// <param name="unstampedFiscalError">
    /// What to record against a sale the handset never stamped. The two paths leave the customer in
    /// different positions — offline, nothing was printed and nothing was declared; online, the server
    /// fiscalised the invoice on a device that is not this van's — so each says its own piece.
    /// </param>
    public static void ApplySignedReceipt(
        this DesktopSaleEntity sale,
        IVanSalesSignedReceipt receipt,
        string unstampedFiscalError)
    {
        var stamped = receipt.ClaimsReceiptSequence();

        // Whether a fiscal receipt exists for this sale — which is not the same question as whether
        // ZIMRA will get it, and is answered by ReceiptIngestStatus below.
        //
        // Success means a number came off the device's chain, so a receipt was printed and the customer
        // is holding it. Never re-fiscalise one of those: the second declaration cannot be withdrawn. A
        // sale from a handset too old to stamp printed nothing of its own, and saying Success there would
        // hide it behind a green tick and keep the one control that could still fix it switched off.
        sale.FiscalizationStatus = stamped
            ? DesktopSaleFiscalizationStatus.Success
            : DesktopSaleFiscalizationStatus.Failed;
        sale.FiscalError = stamped ? null : unstampedFiscalError;

        sale.FiscalDeviceNumber = receipt.FiscalDeviceId;
        sale.FiscalDeviceId =
            int.TryParse(receipt.FiscalDeviceId?.Trim(), out var fiscalDeviceId) && fiscalDeviceId > 0
                ? fiscalDeviceId
                : null;
        sale.FiscalDayNo = receipt.FiscalDayNo?.ToString();
        sale.ReceiptGlobalNo = receipt.ReceiptGlobalNo;
        sale.ReceiptCounter = receipt.ReceiptCounter;
        sale.FiscalVerificationCode = receipt.VerificationCode;
        sale.FiscalQRCode = receipt.QrCode;
        sale.FiscalReceiptNumber = receipt.ReceiptGlobalNo?.ToString();

        // The signed receipt, stored verbatim so it can be handed to the fiscalisation platform and,
        // through it, to ZIMRA. Nothing here is recomputed: the signature covers these exact values.
        sale.ReceiptDate = receipt.ReceiptDate;
        sale.FiscalDayOpenedAt = receipt.FiscalDayOpenedAt;
        sale.PreviousReceiptHash = receipt.PreviousReceiptHash?.Trim();
        sale.DeviceSignatureHash = receipt.DeviceSignatureHash?.Trim();
        sale.DeviceSignatureValue = receipt.DeviceSignatureValue?.Trim();

        // Three outcomes, and the middle one is the one that costs a van its day if it is got wrong.
        // A receipt that is signed goes in the queue. One that consumed a number but cannot be
        // submitted is a hole in the device's chain, and everything signed after it is stuck behind
        // it — that is Unsignable, and the drain stops the device there deliberately. One that was
        // never stamped took no number, so it is not in the chain at all and must not stop anything.
        sale.ReceiptIngestStatus = receipt.HasSignedReceipt()
            ? DesktopSaleReceiptIngestStatus.Pending
            : stamped
                ? DesktopSaleReceiptIngestStatus.Unsignable
                : DesktopSaleReceiptIngestStatus.Unstamped;
    }
}
