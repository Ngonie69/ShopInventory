using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

/// <summary>
/// Turns a raw queue error into a named root cause. A queue full of forty rows is
/// usually two or three actual problems, and the fix is per-problem, not per-row —
/// so every item gets a signature that groups it with the others that share a cause,
/// plus the sentence an operator needs in order to clear the whole group.
/// </summary>
public static class ExceptionCenterErrorClassifier
{
    public sealed record Classification(
        string Signature,
        string Label,
        string Guidance,
        string Family);

    /// <summary>Coarse families, used by the UI for colour and icon.</summary>
    public static class Families
    {
        public const string Sap = "Sap";
        public const string Connectivity = "Connectivity";
        public const string Fiscal = "Fiscal";
        public const string Stock = "Stock";
        public const string Data = "Data";
        public const string Payment = "Payment";
        public const string Worker = "Worker";
        public const string Unknown = "Unknown";
    }

    private sealed record Rule(
        string Signature,
        string Label,
        string Guidance,
        string Family,
        Func<string, bool> Matches);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // Ordered: the first rule that matches wins, so the specific causes come before
    // the general ones (a posting-period rejection also mentions "invoice").
    private static readonly Rule[] Rules =
    [
        new("sap-posting-period",
            "SAP posting period closed for the document date",
            "Unlock the period in SAP (Administration → System Initialisation → Posting Periods). If it is already open, check that the A/R Invoice numbering series is assigned to the same period indicator. Then retry the whole group.",
            Families.Sap,
            text => text.Contains("posting date deviates")
                    || (text.Contains("posting period") && text.Contains("posting date"))
                    || text.Contains("outside the configured posting period")),

        new("sap-circuit-open",
            "SAP circuit breaker tripped",
            "SAP was unreachable long enough to open the breaker, so these never reached it. Confirm the Service Layer is up; items with attempts left drain on their own once it closes.",
            Families.Connectivity,
            text => text.Contains("circuit breaker") || text.Contains("circuit is open")),

        new("sap-session",
            "SAP rejected the session or credentials",
            "The Service Layer login failed or the session expired mid-post. Check the SAP company database, user and password in Settings, then retry.",
            Families.Sap,
            text => text.Contains("invalid session")
                    || text.Contains("-304")
                    || ((text.Contains("login") || text.Contains("logon") || text.Contains("unauthor") || text.Contains("401")) && text.Contains("sap"))),

        new("sap-hana-date-literal",
            "SAP rejected a malformed date literal (-2028)",
            "Raw SQL sent to SAP must format dates as yyyy-MM-dd; yyyyMMdd returns -2028 and an empty result. Fix the caller before retrying — a retry alone will fail the same way.",
            Families.Data,
            text => text.Contains("-2028")),

        new("timeout",
            "SAP Service Layer timed out",
            "The post may or may not have landed in SAP. Check for the document before retrying, so you do not create a duplicate.",
            Families.Connectivity,
            text => text.Contains("timeout") || text.Contains("timed out")),

        new("unreachable",
            "SAP Service Layer unreachable",
            "Network or host level failure — nothing reached SAP, so a retry is safe once connectivity is back.",
            Families.Connectivity,
            text => text.Contains("connection refused")
                    || text.Contains("no such host")
                    || text.Contains("name or service")
                    || text.Contains("unavailable")
                    || text.Contains("502")
                    || text.Contains("503")
                    || text.Contains("504")
                    || (text.Contains("connection") && text.Contains("closed"))),

        new("duplicate-document",
            "Document already exists in SAP",
            "SAP already holds this document, so retrying will not help and may duplicate it. Look the document up by its client request id and close the queue entry.",
            Families.Data,
            text => text.Contains("already exists")
                    || text.Contains("duplicate")
                    || text.Contains("-2035")),

        // "revmax" stays in the match list: incidents raised before the migration still carry it.
        new("fiscalization",
            "Fiscal device rejected the document",
            "Check the fiscal console at https://fiscal.kefaloscheese.com/ and the device certificate. "
                + "Retrying is safe once the device is accepting again — the fiscal number is only allocated on success. "
                + "The exception is an idempotency conflict, where the receipt may already exist: look it up before resubmitting.",
            Families.Fiscal,
            text => text.Contains("fiscal")
                    || text.Contains("revmax")
                    || text.Contains("zimra")
                    || text.Contains("fdms")),

        new("insufficient-stock",
            "Not enough stock in the source warehouse",
            "SAP will keep rejecting this until the stock exists. Reconcile the warehouse or cut the quantity on the source document — a retry on its own cannot clear it.",
            Families.Stock,
            text => (text.Contains("insufficient") && (text.Contains("stock") || text.Contains("quantity")))
                    || text.Contains("not enough")
                    || text.Contains("negative inventory")
                    || text.Contains("quantity falls")),

        new("credit-limit",
            "Customer is over its credit limit",
            "Release the customer's credit hold in SAP or collect against the balance, then retry.",
            Families.Data,
            text => text.Contains("credit limit") || text.Contains("deviation from credit")),

        new("business-partner",
            "Customer master data rejected",
            "The business partner is missing, blocked or incomplete in SAP. Fix the BP record, then retry.",
            Families.Data,
            text => text.Contains("business partner")
                    || text.Contains("cardcode")
                    || text.Contains("customer code")
                    || text.Contains("bp code")),

        new("item-master",
            "Item master data rejected",
            "One or more item codes are missing, inactive or not allowed in the warehouse. Fix the item, then retry.",
            Families.Data,
            text => text.Contains("itemcode")
                    || text.Contains("item code")
                    || (text.Contains("item") && text.Contains("not found"))),

        new("price-tax",
            "Price or tax setup rejected",
            "SAP could not resolve a price list or tax code for a line. Check the price list and tax group on the item and customer, then retry.",
            Families.Data,
            text => text.Contains("tax code")
                    || text.Contains("price list")
                    || text.Contains("vat group")
                    || text.Contains("tax group")),

        new("worker-interrupted",
            "Worker restarted mid-flight",
            "The processor stopped while the item was in flight, so it never got a verdict. Confirm the document did not post, then retry.",
            Families.Worker,
            text => text.Contains("interrupted") || text.Contains("was cancelled by the host")),

        new("operator-cancelled",
            "Cancelled by an operator",
            "Someone cancelled this deliberately. No action needed beyond acknowledging it.",
            Families.Data,
            text => text.StartsWith("cancelled by")),

        new("payment-declined",
            "Payment gateway declined the transaction",
            "The provider rejected the payment; there is nothing to retry on our side. Confirm with the customer and, if they paid, reconcile the callback manually.",
            Families.Payment,
            text => text.Contains("declined")
                    || text.Contains("insufficient funds")
                    || text.Contains("cancelled by user")
                    || text.Contains("rejected by")),
    ];

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex QuotedPattern = new(
        "['\"][^'\"]{1,80}['\"]", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex NumberPattern = new(
        @"\d+", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex WhitespacePattern = new(
        @"\s+", RegexOptions.Compiled, RegexTimeout);

    public static Classification Classify(string? error, string category)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return new Classification(
                "no-error-recorded",
                "No error recorded",
                "The item failed without leaving a message. Check the application log around the failure time before retrying.",
                Families.Unknown);
        }

        var haystack = error.ToLowerInvariant();

        foreach (var rule in Rules)
        {
            if (rule.Matches(haystack))
            {
                return new Classification(rule.Signature, rule.Label, rule.Guidance, rule.Family);
            }
        }

        // Nothing recognised: group by the shape of the message so that a hundred
        // copies of one unknown failure still collapse into one line.
        return new Classification(
            $"unclassified-{Fingerprint(error)}",
            Summarise(error),
            "Not a recognised failure pattern. Read the full error on one item, then decide whether a retry can clear it.",
            FamilyFromCategory(category));
    }

    /// <summary>
    /// Stable short hash of the message with every varying part removed, so
    /// "Invoice 41 failed" and "Invoice 87 failed" land in the same bucket.
    /// </summary>
    private static string Fingerprint(string error)
    {
        var normalized = GuidPattern.Replace(error.ToLowerInvariant(), "#");
        normalized = QuotedPattern.Replace(normalized, "#");
        normalized = NumberPattern.Replace(normalized, "#");
        normalized = WhitespacePattern.Replace(normalized, " ").Trim();

        if (normalized.Length > 160)
        {
            normalized = normalized[..160];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }

    /// <summary>First sentence of the message, trimmed to something a card can hold.</summary>
    private static string Summarise(string error)
    {
        var text = WhitespacePattern.Replace(error, " ").Trim();

        var stop = text.IndexOfAny(['.', ';', '\n']);
        if (stop > 24)
        {
            text = text[..stop];
        }

        return text.Length <= 96 ? text : text[..96].TrimEnd() + "…";
    }

    private static string FamilyFromCategory(string category)
        => category switch
        {
            "SAP Posting" => Families.Sap,
            "Fiscalisation" => Families.Fiscal,
            // Historical incidents were categorised under the decommissioned provider's name.
            "REVMax" => Families.Fiscal,
            "Payment Callback" => Families.Payment,
            "Sync Retry" => Families.Worker,
            _ => Families.Unknown
        };
}
