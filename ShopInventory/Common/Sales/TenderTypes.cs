using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Which SAP incoming-payment sum an amount belongs in.
/// </summary>
/// <remarks>
/// SAP models payment means as parallel sums on one document rather than as a type field, so a tender
/// is expressed by choosing which sum to fill. <c>Check</c> exists on the SAP document too but no till
/// tender maps to it, so it is not represented here.
/// </remarks>
public enum PaymentSum
{
    /// <summary>CashSum.</summary>
    Cash,

    /// <summary>CreditSum, alongside a PaymentCreditCards line.</summary>
    Credit,

    /// <summary>TransferSum, alongside TransferReference and TransferDate.</summary>
    Transfer
}

/// <summary>
/// The tenders a shop till accepts, and where each one lands downstream.
///
/// A tender has to be carried to three places and they disagree about vocabulary: ZIMRA wants a
/// <see cref="MoneyType"/> on the fiscal receipt, SAP wants one of several parallel sums on the
/// incoming payment, and the sale row stores the brand the customer actually paid with. This type is
/// the single place those three are related, so the till and the handlers cannot drift apart on how
/// "Innbucks" is spelled.
///
/// Vending sells for cash only; the four tenders below are the shop set.
/// </summary>
public static class TenderTypes
{
    public const string Cash = "Cash";
    public const string Swipe = "Swipe";
    public const string Ecocash = "Ecocash";
    public const string Innbucks = "Innbucks";

    /// <summary>The tenders a till may submit. Anything else is rejected on a new sale.</summary>
    public static readonly string[] SupportedTenders =
    [
        Cash,
        Swipe,
        Ecocash,
        Innbucks
    ];

    /// <summary>
    /// Whether the tender is one a till may submit today.
    /// </summary>
    public static bool IsSupported(string? tender)
        => TryNormalize(tender, out _);

    /// <summary>
    /// Normalises a till-supplied tender to its canonical spelling.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: it accepts casing and spacing differences and the obvious synonyms for the
    /// card reader, but it does not accept the legacy stored values (<c>transfer</c>, <c>paynow</c>).
    /// Those are history that <see cref="ToMoneyType"/> and <see cref="ToPaymentSum"/> still have to
    /// read, but nothing new should be written with them.
    /// </remarks>
    public static bool TryNormalize(string? tender, out string normalizedTender)
    {
        normalizedTender = string.Empty;

        if (string.IsNullOrWhiteSpace(tender))
        {
            return false;
        }

        var sanitized = tender.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

        normalizedTender = sanitized.ToLowerInvariant() switch
        {
            "cash" => Cash,
            "swipe" or "card" or "swipemachine" or "creditcard" or "debitcard" => Swipe,
            "ecocash" => Ecocash,
            "innbucks" or "inbucks" => Innbucks,
            _ => string.Empty
        };

        return normalizedTender.Length > 0;
    }

    /// <summary>
    /// Whether the tender cannot be reconciled without an external reference from the customer.
    /// </summary>
    /// <remarks>
    /// The mobile wallets settle outside the till, so the confirmation reference is the only thing
    /// tying a receipt to money that arrived. Cash needs nothing, and a swipe slip is useful but the
    /// terminal settlement already carries the link.
    /// </remarks>
    public static bool RequiresReference(string? tender)
        => TryNormalize(tender, out var normalized) && normalized is Ecocash or Innbucks;

    /// <summary>
    /// The money type declared to ZIMRA for this tender, or <c>null</c> if the tender is absent or
    /// unrecognised.
    /// </summary>
    /// <remarks>
    /// Returns null rather than defaulting, because the fiscal receipt is a declaration: guessing
    /// "cash" for a sale nobody recorded a tender for would be a false statement to the revenue
    /// authority, and a caller should have to choose that fallback in the open. Legacy stored values
    /// are recognised so historical rows still map — Ecocash and Innbucks are both wallets, and both
    /// <c>transfer</c> and <c>paynow</c> settled the same way.
    /// </remarks>
    public static MoneyType? ToMoneyType(string? tender)
    {
        if (string.IsNullOrWhiteSpace(tender))
        {
            return null;
        }

        if (TryNormalize(tender, out var normalized))
        {
            return normalized switch
            {
                Cash => MoneyType.Cash,
                Swipe => MoneyType.Card,
                Ecocash or Innbucks => MoneyType.MobileWallet,
                _ => null
            };
        }

        return NormalizeLegacy(tender) switch
        {
            LegacyTender.Cash => MoneyType.Cash,
            LegacyTender.Transfer => MoneyType.MobileWallet,
            _ => null
        };
    }

    /// <summary>
    /// Which SAP incoming-payment sum this tender's amount belongs in, or <c>null</c> if the tender is
    /// absent or unrecognised.
    /// </summary>
    /// <remarks>
    /// Ecocash and Innbucks both resolve to <see cref="PaymentSum.Transfer"/> and so share one SAP
    /// account under the system's default posting. They stay distinguishable by the transfer reference
    /// and by the sale row's own payment method; splitting them in SAP is a matter of supplying
    /// different transfer accounts, not of changing this mapping.
    /// </remarks>
    public static PaymentSum? ToPaymentSum(string? tender)
    {
        if (string.IsNullOrWhiteSpace(tender))
        {
            return null;
        }

        if (TryNormalize(tender, out var normalized))
        {
            return normalized switch
            {
                Cash => PaymentSum.Cash,
                Swipe => PaymentSum.Credit,
                Ecocash or Innbucks => PaymentSum.Transfer,
                _ => null
            };
        }

        return NormalizeLegacy(tender) switch
        {
            LegacyTender.Cash => PaymentSum.Cash,
            LegacyTender.Transfer => PaymentSum.Transfer,
            _ => null
        };
    }

    private enum LegacyTender
    {
        Unknown,
        Cash,
        Transfer
    }

    /// <summary>
    /// The payment methods written before the till captured a tender, still present on historical
    /// sale rows.
    /// </summary>
    private static LegacyTender NormalizeLegacy(string tender)
        => tender.Trim().ToLowerInvariant() switch
        {
            "cash" => LegacyTender.Cash,
            "transfer" or "paynow" => LegacyTender.Transfer,
            _ => LegacyTender.Unknown
        };
}
