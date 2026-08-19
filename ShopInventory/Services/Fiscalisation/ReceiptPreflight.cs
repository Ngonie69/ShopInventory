using System.Globalization;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Decides whether a receipt will be accepted, before it is sent.
///
/// FDMS validation failures are cheap to prevent and expensive to discover. On the server-signed path a
/// rejection costs a round trip and an error nobody can read — the platform masks most of them behind one
/// generic sentence. On the pre-signed path it costs far more: the receipt has already been signed on a
/// handset and handed to a customer, its number is spent from the device's chain, and every later receipt
/// from that handset is stuck behind it. Nothing can be corrected after signing, because correcting it
/// changes the payload the signature covers.
///
/// So the rules are checked twice, at the two moments where checking still helps. Once when a handset
/// collects its lease — the last point at which a bad receipt can be prevented rather than diagnosed —
/// and once before submission, so a receipt that cannot succeed is reported precisely instead of spending
/// a device's retries.
/// </summary>
/// <remarks>
/// These mirror the platform's <c>SubmitReceiptPreflightValidator</c>, deliberately down to the error code
/// each one prevents, so a finding here reads the same as the failure it is avoiding. It is a mirror and
/// not the authority: rules needing state only the platform holds — whether an invoice number is already
/// archived, what the next counter is, whether FDMS considers the day open — are checked there, through
/// <c>IFiscalisationApiClient.PreflightSignedReceiptAsync</c>. What is here is everything decidable from
/// the receipt and the device's own configuration, which is the part that still works with no signal.
/// </remarks>
public static class ReceiptPreflight
{
    // Ceilings copied from the platform's ReceiptInputLimits. Its choices, not FDMS's — the platform
    // refuses beyond these before FDMS is ever asked.
    private const int MaxReceiptLines = 500;
    private const int MaxInvoiceNumberLength = 50;
    private const int MaxLineNameLength = 200;
    private const int MaxTaxCodeLength = 3;
    private const int BuyerTinLength = 10;
    private const int BuyerVatNumberLength = 9;
    private const int MaxBuyerNameLength = 250;

    /// <summary>How close a certificate may come to expiry before it is worth saying so.</summary>
    private const int CertificateExpiryWarningDays = 21;

    /// <summary>
    /// Checks a receipt the platform will sign.
    /// </summary>
    public static PreflightReport Inspect(SubmitReceiptApiRequest request, PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<PreflightFinding>();
        InspectCommon(request, context, findings, preSigned: false);
        return new PreflightReport(findings);
    }

    /// <summary>
    /// Checks a receipt a handset already signed, including the parts only a signed receipt has: its
    /// place in the device's chain, and whether the signature covers what was actually sent.
    /// </summary>
    public static PreflightReport InspectSigned(IngestSignedReceiptApiRequest request, PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<PreflightFinding>();
        InspectCommon(request, context, findings, preSigned: true);
        InspectDeviceMode(context, findings);
        InspectSequence(request, context, findings);
        InspectSignature(request, findings);
        return new PreflightReport(findings);
    }

    private static void InspectCommon(
        SubmitReceiptApiRequest request,
        PreflightContext context,
        List<PreflightFinding> findings,
        bool preSigned)
    {
        var config = context.Config;

        if (config is null)
        {
            // Not a failure of the receipt. Most rules below need the device's live configuration —
            // which taxes it may use, whether the taxpayer is VAT registered — and guessing at them
            // would either invent rejections or, worse, wave through a receipt nothing has checked.
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "ConfigUnavailable",
                "The device's configuration could not be read, so most rules were not checked. " +
                "The platform will validate the receipt again on arrival."));
        }

        InspectDocument(request, findings, preSigned);
        InspectCertificate(context, findings);
        InspectFiscalDay(context, findings);

        if (request.Lines.Count == 0)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                "The receipt has no lines. The platform rebuilds the receipt from them, so there is nothing to fiscalise."));
            return;
        }

        if (request.Lines.Count > MaxReceiptLines)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                $"The receipt has {request.Lines.Count} lines; the platform accepts at most {MaxReceiptLines}."));
        }

        var derived = FiscalReceiptDerivation.Derive(request);
        InspectLines(request, derived, config, findings, preSigned);
        InspectVatTaxpayerUsage(request, config, findings);
        InspectBuyer(request, findings);
        InspectTotals(request, derived, findings);
    }

    private static void InspectDocument(
        SubmitReceiptApiRequest request,
        List<PreflightFinding> findings,
        bool preSigned)
    {
        if (string.IsNullOrWhiteSpace(request.InvoiceNo))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "IDEMPOTENCY_KEY_REQUIRED",
                "The receipt carries no invoice number. It is the platform's idempotency key, so without " +
                "it a resubmission cannot be recognised as the same receipt."));
        }
        else if (request.InvoiceNo.Length > MaxInvoiceNumberLength)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                $"The invoice number is {request.InvoiceNo.Length} characters; the platform accepts at most {MaxInvoiceNumberLength}."));
        }

        var currency = request.Currency;

        if (string.IsNullOrWhiteSpace(currency))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "RCPT010",
                "The receipt names no currency."));
        }
        else if (preSigned)
        {
            // The two paths differ here, and it is the difference most likely to bite. A server-signed
            // submission is normalised on arrival — trimmed, upper-cased, ZIG rewritten to ZWG — but a
            // pre-signed one cannot be: the currency is inside the signed payload, so touching it would
            // invalidate the signature. What the handset signed is what FDMS is asked to accept.
            var trimmed = currency.Trim();

            if (!string.Equals(currency, trimmed, StringComparison.Ordinal) ||
                !string.Equals(trimmed, trimmed.ToUpperInvariant(), StringComparison.Ordinal))
            {
                findings.Add(new PreflightFinding(
                    PreflightSeverity.Block,
                    "RCPT010",
                    $"The currency '{currency}' is not already normalised. A pre-signed receipt is not " +
                    "normalised by the platform, because the currency is part of what was signed — the " +
                    "handset must send it trimmed and upper-cased."));
            }
            else if (string.Equals(trimmed, "ZIG", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new PreflightFinding(
                    PreflightSeverity.Block,
                    "RCPT010",
                    "FDMS knows the Zimbabwe Gold as ZWG and rejects ZIG. SAP calls it ZiG, so the " +
                    "handset must convert before signing, not after."));
            }
        }

        if (preSigned && request.ReceiptDate is null or { Ticks: 0 })
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                "The receipt carries no date, and its date is part of the signed payload."));
        }
    }

    private static void InspectCertificate(PreflightContext context, List<PreflightFinding> findings)
    {
        if (context.Config is not { } config || config.CertificateValidTill == default)
        {
            return;
        }

        var remaining = config.CertificateValidTill - context.NowLocal;

        if (remaining <= TimeSpan.Zero)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "DeviceCertificateExpired",
                $"The device's certificate expired on {config.CertificateValidTill:yyyy-MM-dd}. It cannot " +
                "sign, and a handset holding this device will stop trading until it is renewed."));
        }
        else if (remaining.TotalDays <= CertificateExpiryWarningDays)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "DeviceCertificateExpiring",
                $"The device's certificate expires on {config.CertificateValidTill:yyyy-MM-dd}, in " +
                $"{(int)remaining.TotalDays} day(s). Renewal needs ZIMRA, so it is not a same-day job."));
        }
    }

    private static void InspectFiscalDay(PreflightContext context, List<PreflightFinding> findings)
    {
        if (context.FiscalDayOpen == false)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "RCPT041",
                "The device's fiscal day is not open, so a receipt cannot be signed into it."));
        }

        if (context.Config is not { TaxPayerDayMaxHrs: > 0 } config ||
            context.FiscalDayOpenedAt is not { } openedAt)
        {
            return;
        }

        var elapsed = context.NowLocal - openedAt;

        if (elapsed >= TimeSpan.FromHours(config.TaxPayerDayMaxHrs))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "RCPT041",
                $"The fiscal day opened at {openedAt:yyyy-MM-dd HH:mm} and has run {elapsed.TotalHours:F1} " +
                $"hours, past this taxpayer's {config.TaxPayerDayMaxHrs}-hour limit. It must be closed " +
                "before anything else is signed into it."));
        }
        else if (context.WarnAtPercentOfMaxHrs > 0 &&
                 elapsed.TotalHours >= config.TaxPayerDayMaxHrs * context.WarnAtPercentOfMaxHrs / 100d)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "FiscalDayNearingLimit",
                $"The fiscal day has run {elapsed.TotalHours:F1} of its {config.TaxPayerDayMaxHrs} hours. " +
                "Close it before the limit, or the receipts already signed into it are at risk."));
        }
    }

    private static void InspectDeviceMode(PreflightContext context, List<PreflightFinding> findings)
    {
        if (context.Config is not { } config || string.IsNullOrWhiteSpace(config.DeviceOperatingMode))
        {
            return;
        }

        if (!string.Equals(config.DeviceOperatingMode.Trim(), "Offline", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "DEV01",
                $"Device {config.DeviceSerialNo} is registered with ZIMRA in {config.DeviceOperatingMode} " +
                "mode, and only an Offline-mode device may hand over receipts it signed itself. In " +
                "Online mode FDMS owns the sequence, so the handset's numbering is not the device's."));
        }
    }

    private static void InspectLines(
        SubmitReceiptApiRequest request,
        DerivedReceipt derived,
        FiscalConfigApiResponse? config,
        List<PreflightFinding> findings,
        bool preSigned)
    {
        var isVatPayer = config is not null && !string.IsNullOrWhiteSpace(config.VatNumber);
        var isNote = request.ReceiptType is ReceiptType.CreditNote or ReceiptType.DebitNote;

        foreach (var line in derived.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Name))
            {
                findings.Add(Line(line, "ValidationFailed", "has no name."));
            }
            else if (line.Name.Length > MaxLineNameLength)
            {
                findings.Add(Line(line, "ValidationFailed",
                    $"has a {line.Name.Length}-character name; the platform accepts at most {MaxLineNameLength}."));
            }

            if (line.Quantity <= 0m)
            {
                findings.Add(Line(line, "ValidationFailed", $"has a quantity of {line.Quantity}, which must be above zero."));
            }

            // The platform refuses a credit note whose prices are not negative, and an invoice or debit
            // note whose prices are not positive. The sign is the whole difference between charging a
            // customer and refunding one.
            if (request.ReceiptType == ReceiptType.CreditNote && line.Price >= 0m)
            {
                findings.Add(Line(line, "ValidationFailed",
                    $"is priced {line.Price} on a credit note, where prices must be negative."));
            }
            else if (request.ReceiptType != ReceiptType.CreditNote && line.Price <= 0m)
            {
                findings.Add(Line(line, "ValidationFailed",
                    $"is priced {line.Price} on a {request.ReceiptType}, where prices must be positive."));
            }

            // Only on the path that enforces it. The platform's 3-character ceiling lives in
            // SubmitReceiptCommandValidator, which the pre-signed path never runs — and it could not
            // apply there anyway, because the tax code is inside the signed payload and cannot be
            // shortened after the fact.
            //
            // Which is just as well: the van lease hands the handset the FDMS tax *name* as its code
            // ("15.5% Output VAT USD"), so every van receipt exceeds this. Applying the rule to signed
            // receipts would refuse the entire fleet's takings over a limit that does not govern them.
            if (!preSigned && line.TaxCode is { Length: > MaxTaxCodeLength })
            {
                findings.Add(Line(line, "ValidationFailed",
                    $"has tax code '{line.TaxCode}'; the platform accepts at most {MaxTaxCodeLength} characters."));
            }

            InspectLineTax(line, config, findings);

            if (!isNote)
            {
                InspectLineHsCode(line, isVatPayer, findings);
            }
        }
    }

    private static void InspectLineTax(
        DerivedLine line,
        FiscalConfigApiResponse? config,
        List<PreflightFinding> findings)
    {
        if (config is null || config.ApplicableTaxes.Count == 0)
        {
            return;
        }

        var candidates = config.ApplicableTaxes.Where(tax => tax.TaxID == line.TaxId).ToList();

        if (candidates.Count == 0)
        {
            findings.Add(Line(line, "RCPT025",
                $"uses tax id {line.TaxId}, which this device does not have. Its taxes are " +
                $"{DescribeTaxes(config)}."));
            return;
        }

        // The percentage has to match the id, not merely be plausible. FDMS accepts a mismatched pair
        // without complaint and files the line under the wrong rate — the failure mode is an accepted
        // receipt that under-declares VAT, which no error message will ever tell you about.
        if (!candidates.Any(tax => tax.TaxPercent == line.TaxPercent))
        {
            findings.Add(Line(line, "RCPT025",
                $"declares tax id {line.TaxId} at {FormatPercent(line.TaxPercent)}, but that id is " +
                $"{string.Join(" or ", candidates.Select(tax => FormatPercent(tax.TaxPercent)))} on this device."));
        }
    }

    private static void InspectLineHsCode(DerivedLine line, bool isVatPayer, List<PreflightFinding> findings)
    {
        var hsCode = line.HsCode;

        if (string.IsNullOrWhiteSpace(hsCode))
        {
            if (isVatPayer)
            {
                findings.Add(Line(line, "RCPT047", "has no HS code, and a VAT-registered taxpayer must give one on every invoice line."));
            }

            return;
        }

        if (!hsCode.All(char.IsAsciiDigit))
        {
            findings.Add(Line(line, "RCPT048", $"has HS code '{hsCode}', which must be digits only."));
            return;
        }

        // A zero-rated or exempt line is the one place the code must be the full eight digits: it is
        // what ZIMRA uses to justify the relief.
        var requiresEightDigits = isVatPayer && line.TaxPercent is null or 0m;

        if (requiresEightDigits)
        {
            if (hsCode.Length != 8)
            {
                findings.Add(Line(line, "RCPT048",
                    $"has a {hsCode.Length}-digit HS code; a zero-rated or exempt line from a VAT payer needs 8."));
            }
        }
        else if (hsCode.Length is not (4 or 8))
        {
            findings.Add(Line(line, "RCPT048", $"has a {hsCode.Length}-digit HS code, which must be 4 or 8."));
        }
    }

    private static void InspectVatTaxpayerUsage(
        SubmitReceiptApiRequest request,
        FiscalConfigApiResponse? config,
        List<PreflightFinding> findings)
    {
        if (config is null ||
            !string.IsNullOrWhiteSpace(config.VatNumber) ||
            request.ReceiptType is ReceiptType.CreditNote or ReceiptType.DebitNote)
        {
            return;
        }

        if (request.Lines.Any(line => line.TaxPercent is > 0m))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "RCPT021",
                "This taxpayer is not VAT registered, so a fiscal invoice may not charge a VAT rate above zero."));
        }
    }

    private static void InspectBuyer(SubmitReceiptApiRequest request, List<PreflightFinding> findings)
    {
        if (request.Buyer is not { } buyer)
        {
            return;
        }

        if (buyer.RegisterName is { Length: > MaxBuyerNameLength })
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                $"The buyer's registered name is {buyer.RegisterName.Length} characters; at most {MaxBuyerNameLength} are accepted."));
        }

        if (!string.IsNullOrWhiteSpace(buyer.Tin) && buyer.Tin.Trim().Length != BuyerTinLength)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                $"The buyer's TIN '{buyer.Tin}' is {buyer.Tin.Trim().Length} characters; a Zimbabwean TIN is {BuyerTinLength}."));
        }

        if (!string.IsNullOrWhiteSpace(buyer.VatNumber) && buyer.VatNumber.Trim().Length != BuyerVatNumberLength)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                $"The buyer's VAT number '{buyer.VatNumber}' is {buyer.VatNumber.Trim().Length} characters; a Zimbabwean VAT number is {BuyerVatNumberLength}."));
        }

        // The platform drops a buyer that has a name but no TIN, and one that has a TIN but no name,
        // rather than refusing the receipt. Worth saying, because the receipt is then archived without
        // the buyer the operator thought they had entered.
        if (!string.IsNullOrWhiteSpace(buyer.RegisterName) != !string.IsNullOrWhiteSpace(buyer.Tin))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "BuyerIncomplete",
                "The buyer has only one of a registered name and a TIN. The platform records a buyer only " +
                "when it has both, so this receipt will be archived with no buyer at all."));
        }
    }

    private static void InspectTotals(
        SubmitReceiptApiRequest request,
        DerivedReceipt derived,
        List<PreflightFinding> findings)
    {
        if (derived.ReceiptTotal == 0m)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                "The receipt totals zero once the platform recomputes it from the lines."));
        }

        // The platform anchors the payment to the total it derives and ignores whatever was sent, so a
        // disagreement is not rejected — it is silently overwritten. That matters when the figure came
        // from a till or a SAP document: the receipt then declares something other than what was taken.
        if (request.PaymentAmount != 0m && request.PaymentAmount != derived.ReceiptTotal)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "PaymentTotalMismatch",
                $"The payment says {request.PaymentAmount:0.00} but the lines derive {derived.ReceiptTotal:0.00}. " +
                "The platform will declare its own figure, so the receipt will not match what was taken."));
        }
    }

    private static void InspectSequence(
        IngestSignedReceiptApiRequest request,
        PreflightContext context,
        List<PreflightFinding> findings)
    {
        if (request.FiscalDayNo <= 0)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "ValidationFailed",
                "The receipt names no fiscal day, and the platform cannot infer a day it never opened."));
        }

        if (request.ReceiptCounter <= 0)
        {
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "RCPT011", "The receipt has no counter."));
        }

        if (request.ReceiptGlobalNo <= 0)
        {
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "RCPT012", "The receipt has no global number."));
        }

        if (context.LastReceiptGlobalNo is { } lastGlobalNo &&
            request.ReceiptGlobalNo > 0 &&
            request.ReceiptGlobalNo <= lastGlobalNo)
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "RCPT012",
                $"Global number {request.ReceiptGlobalNo} is not past the last one the device recorded " +
                $"({lastGlobalNo}). Either it has already been archived, or two handsets are signing on " +
                "this device — which forks its chain and voids the fiscal day."));
        }

        if (request.ReceiptCounter == 1 && !string.IsNullOrWhiteSpace(request.PreviousReceiptHash))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "ChainStartUnexpected",
                "This is counter 1, the first receipt of a fiscal day, yet it chains onto a previous hash. " +
                "A day starts a fresh chain, so the platform will expect none."));
        }
    }

    /// <summary>
    /// Recomputes the payload the handset should have signed and compares it against the hash it sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mismatch is reported as a warning and never blocks, which is the opposite of what it first
    /// looks like it should be. The reasoning is worth keeping, because "tighten this to a block" is a
    /// tempting and destructive change.
    /// </para>
    /// <para>
    /// What the signature covers is what the handset held at the moment it signed. What is compared here
    /// is a reconstruction of that from database columns — a line's price after storage rounding, a tax
    /// percent that may have been stored as null where the handset had zero, lines read back in a
    /// different order. The derivation itself is pinned against the platform's golden vectors, so the
    /// algorithm is right; the inputs are the part that can drift, and they drift silently.
    /// </para>
    /// <para>
    /// Blocking on that would be the worst possible failure mode. A block here marks the receipt
    /// permanently unsubmittable, and because a device's receipts are chained, that stops every later
    /// receipt from the same handset — so a single storage-fidelity bug would halt the whole fleet's
    /// fiscalisation, irreversibly, on a verdict this application is not the authority for. The platform
    /// holds the certificate and does the real verification; if it refuses, that refusal is authoritative
    /// and is already handled. This check exists to say so first, and to point at the cause.
    /// </para>
    /// </remarks>
    private static void InspectSignature(IngestSignedReceiptApiRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceSignatureHash) ||
            string.IsNullOrWhiteSpace(request.DeviceSignatureValue))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Block,
                "SignatureMissing",
                "The receipt carries no device signature, so there is nothing for the platform to verify. " +
                "Its number is spent on the handset, so every later receipt from it is blocked behind this one."));
            return;
        }

        if (request.ReceiptDate is not { } receiptDate || request.ReceiptGlobalNo <= 0 || request.Lines.Count == 0)
        {
            // Already reported above; recomputing over missing fields would only add noise.
            return;
        }

        var derived = FiscalReceiptDerivation.Derive(request);

        var expectedPayload = FiscalReceiptDerivation.BuildCanonicalPayload(
            request.DeviceId,
            request.ReceiptType,
            request.Currency,
            request.ReceiptGlobalNo,
            receiptDate,
            derived.ReceiptTotal,
            derived.Taxes,
            request.PreviousReceiptHash);

        var expectedHash = FiscalReceiptDerivation.ComputeSignatureHash(expectedPayload);

        if (!string.Equals(expectedHash, request.DeviceSignatureHash.Trim(), StringComparison.Ordinal))
        {
            findings.Add(new PreflightFinding(
                PreflightSeverity.Warn,
                "SignaturePayloadMismatch",
                "The signature hash does not cover this receipt as rebuilt from what was stored, so the " +
                "platform is likely to refuse it as tampered. Either a field changed after signing, the " +
                "stored lines are not a faithful copy of the signed ones, or the handset derives the " +
                "payload differently — compare its port against the golden vectors. Submitting anyway: " +
                "the platform holds the certificate and its verdict is the one that counts."));
        }
    }

    private static PreflightFinding Line(DerivedLine line, string code, string problem) =>
        new(PreflightSeverity.Block, code, $"Line {line.LineNo} ({Describe(line.Name)}) {problem}");

    private static string Describe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();

    private static string DescribeTaxes(FiscalConfigApiResponse config) =>
        string.Join(", ", config.ApplicableTaxes
            .OrderBy(tax => tax.TaxID)
            .Select(tax => $"{tax.TaxID} ({FormatPercent(tax.TaxPercent)})"));

    private static string FormatPercent(decimal? percent) =>
        percent.HasValue
            ? percent.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : "untaxed";
}

/// <summary>
/// What the checks are allowed to assume about the device and the moment.
/// </summary>
/// <param name="Config">
/// The device's live configuration. Null when it could not be read, which downgrades most rules rather
/// than failing them.
/// </param>
/// <param name="NowLocal">
/// The taxpayer's wall clock, not UTC. Fiscal day arithmetic is stated in local terms and a UTC value
/// moves the deadline by the offset.
/// </param>
/// <param name="FiscalDayOpen">Whether the day is open, when that is known.</param>
/// <param name="FiscalDayOpenedAt">When it opened, in the same local clock.</param>
/// <param name="LastReceiptGlobalNo">The last global number the device is known to have recorded.</param>
/// <param name="WarnAtPercentOfMaxHrs">How far into the day's permitted length to start warning.</param>
public sealed record PreflightContext(
    FiscalConfigApiResponse? Config,
    DateTime NowLocal,
    bool? FiscalDayOpen = null,
    DateTime? FiscalDayOpenedAt = null,
    int? LastReceiptGlobalNo = null,
    int WarnAtPercentOfMaxHrs = 80);

public enum PreflightSeverity
{
    /// <summary>Worth saying, but not a reason to hold the receipt.</summary>
    Warn = 0,

    /// <summary>The platform or FDMS will refuse this. Sending it wastes an attempt at best.</summary>
    Block = 1
}

/// <summary>One thing that is wrong, or worth knowing, about a receipt.</summary>
/// <param name="Severity">Whether this holds the receipt back or is merely worth saying.</param>
/// <param name="Code">
/// The error this prevents, using the platform's own vocabulary so a finding and the failure it avoids
/// read the same.
/// </param>
/// <param name="Message">What is wrong, in terms an operator can act on.</param>
public sealed record PreflightFinding(PreflightSeverity Severity, string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

public sealed record PreflightReport(IReadOnlyList<PreflightFinding> Findings)
{
    public static PreflightReport Clear { get; } = new([]);

    public bool IsBlocked => Findings.Any(finding => finding.Severity == PreflightSeverity.Block);

    public IEnumerable<PreflightFinding> Blocks =>
        Findings.Where(finding => finding.Severity == PreflightSeverity.Block);

    public IEnumerable<PreflightFinding> Warnings =>
        Findings.Where(finding => finding.Severity == PreflightSeverity.Warn);

    /// <summary>The blocking reasons as one line, for a log or a stored error.</summary>
    public string BlockSummary => string.Join(" ", Blocks.Select(finding => finding.ToString()));

    /// <summary>Every finding as one line, for an operator reading the console.</summary>
    public string Summary => string.Join(" ", Findings.Select(finding => finding.ToString()));

    public PreflightReport Merge(PreflightReport other) =>
        other.Findings.Count == 0 ? this : new PreflightReport([.. Findings, .. other.Findings]);
}
