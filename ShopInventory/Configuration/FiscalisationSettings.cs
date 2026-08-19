namespace ShopInventory.Configuration;

/// <summary>
/// Configuration for the ZIMRA FDMS Fiscalisation platform that replaced REVMax.
/// </summary>
public class FiscalisationSettings
{
    public const string SectionName = "Fiscalisation";

    /// <summary>
    /// Whether fiscalisation is enabled. When false, fiscalisation is skipped and reported as such
    /// rather than failing the document that triggered it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL for the Fiscalisation console API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://fiscal.kefaloscheese.com/";

    /// <summary>
    /// API key issued from the console's API Keys page. Sent as the X-API-Key header.
    /// Needs the receipt.submit, sap.fiscalise and device.read scopes, and no device allowlist
    /// (a device-scoped key forces an explicit device id on every call, which breaks failover).
    /// Supply via user-secrets locally and the Fiscalisation__ApiKey environment variable in production.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Pins fiscalisation to one device. Leave unset — the default — to use any device the console has.
    /// </summary>
    /// <remarks>
    /// Unset is the setting we want, and it is not merely a fallback. A submission that names no device
    /// makes the platform walk every device it has configured, in order, and fiscalise on the first one
    /// that takes the receipt: a device whose certificate has expired, whose fiscal day will not open,
    /// or that FDMS is refusing simply steps aside for the next. It only ever moves on when it knows
    /// FDMS recorded nothing, so failing over cannot duplicate a receipt, and it will not cross to a
    /// device registered to a different taxpayer. Naming a device here throws all of that away and
    /// fiscalisation stops with it.
    ///
    /// The device that actually took the receipt comes back on the response, so the QR payload and the
    /// serial on the document follow the failover rather than this setting.
    ///
    /// Set it only to force one device deliberately, and expect no failover while it is set. Reads —
    /// the fiscal configuration and day status — treat 0 as "the console's own device" instead, since
    /// there is nothing to fail over on a lookup.
    /// </remarks>
    public int DefaultDeviceId { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// How many times a request rejected *before* it reached FDMS is retried. Only those are retried;
    /// see <see cref="Services.Fiscalisation.FiscalisationApiClient.IsSafeToRetry"/>.
    /// </summary>
    public int TransientRetryCount { get; set; } = 10;

    public int TransientRetryBaseDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Default currency code for receipts submitted without one.
    /// </summary>
    public string DefaultCurrency { get; set; } = "USD";

    /// <summary>
    /// SAP tax code to FDMS TaxID, for the pre-SAP path only. Configure in appsettings.
    /// </summary>
    /// <remarks>
    /// Deliberately empty here rather than carrying plausible-looking defaults. FDMS tax ids are
    /// specific to one taxpayer on one FDMS environment — the ids for the ZIMRA test service are not
    /// the ids for the live one — so a default that looks reasonable is a default that is wrong
    /// somewhere. Empty falls through to <see cref="DefaultTaxId"/>, and an unset DefaultTaxId is
    /// rejected by the platform's own validation, which is the loud failure we want.
    ///
    /// The authoritative list is the device's active taxes from /api/fiscal-config. The Fiscalisation
    /// platform keeps the equivalent mapping for its SAP path in SapServiceLayer:TaxMappings; keep the
    /// two in step.
    /// </remarks>
    public Dictionary<string, int> TaxIdMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// FDMS TaxID for lines whose SAP tax code is not in <see cref="TaxIdMappings"/>.
    /// </summary>
    public int DefaultTaxId { get; set; }

    /// <summary>
    /// HS code used when a line carries none. FDMS requires one on every invoice line for a
    /// VAT-registered taxpayer, and it must be 4 or 8 digits. Credit and debit notes are exempt.
    /// </summary>
    public string? DefaultHsCode { get; set; }

    /// <summary>
    /// Prefix applied to a purely numeric pre-SAP invoice number.
    /// </summary>
    /// <remarks>
    /// The platform's idempotency key is (taxpayer, receipt type, invoice number), and invoices posted
    /// from SAP key on their SAP DocNum. A bare numeric external reference therefore shares a namespace
    /// with every present and future DocNum: when SAP later issues that number, the two documents
    /// collide on one key and the second is refused, or worse, silently answered with the first one's
    /// receipt. Prefixing lifts pre-SAP receipts out of that namespace.
    /// </remarks>
    public string PreSapInvoiceNoPrefix { get; set; } = "SI-";

    /// <summary>
    /// The SAP user-defined fields this integration reads and writes on a marketing document.
    /// </summary>
    public FiscalisationUdfSettings Udf { get; set; } = new();

    /// <summary>
    /// Refuses a van sale that arrives without a signed fiscal receipt.
    /// </summary>
    /// <remarks>
    /// Off during a fleet rollout, and that is not laxity: a handset on a build older than the signing
    /// release cannot produce a receipt, and refusing its sales stops the van trading rather than making
    /// it compliant. While it is off, an unstamped sale is accepted and recorded as
    /// <see cref="Models.Entities.DesktopSaleReceiptIngestStatus.Unstamped"/>, so the tail of un-updated
    /// handsets is visible and shrinking.
    ///
    /// Two surfaces show it, and they are the only two — there is no van-sales report of exceptions, and
    /// nothing under <c>Features/VanSalesReports/Queries/</c> counts these. The fiscalisation console at
    /// <c>/fiscalisation</c> in the Web app counts unstamped receipts per device and lists them under the
    /// work queue's "Never stamped" filter; the Exception Center carries the same rows under
    /// <c>fiscal-receipt-ingest</c>, marked as a handset that needs updating rather than a receipt that
    /// is stuck.
    ///
    /// Turn it on once that count reaches zero. After that an unstamped sale is a bug, not a straggler.
    /// </remarks>
    public bool RequireStampedVanSales { get; set; }

    public FiscalisationPreflightSettings Preflight { get; set; } = new();

    public FiscalDaySettings FiscalDay { get; set; } = new();
}

/// <summary>
/// Which SAP user-defined fields carry the mobile invoice number and the idempotency key.
/// </summary>
/// <remarks>
/// Named in configuration rather than hard-coded because the two fields are not on the same schedule.
/// <see cref="IdempotencyField"/> exists today and is in use; <see cref="InvoiceNumberField"/> is being
/// added to the production company and is deliberately dormant until it is, since posting an undefined
/// UDF makes the Service Layer refuse the whole document.
/// </remarks>
public class FiscalisationUdfSettings
{
    /// <summary>
    /// The UDF holding the invoice number the mobile device generated — <c>U_Comex</c> in production.
    /// </summary>
    /// <remarks>
    /// Blank means dormant: nothing is written and nothing is read, and
    /// <see cref="IdempotencyField"/> alone answers "has this sale already been posted".
    ///
    /// Once it is named, it becomes the single fiscal identity of the sale. The same string is written
    /// to SAP and sent to the platform as the receipt's InvoiceNo, which is what stops one sale being
    /// fiscalised twice under two different numbers — once as the handset's reference on the offline
    /// path and once as the SAP DocNum on the SAP path. The platform's idempotency key is
    /// (taxpayer, receipt type, invoice number) and cannot see that those are the same sale.
    /// </remarks>
    public string InvoiceNumberField { get; set; } = string.Empty;

    /// <summary>
    /// The UDF carrying the sale's own reference, used to recognise a document SAP already holds.
    /// </summary>
    /// <remarks>
    /// Read before posting, not only while recovering from a failed one. A post whose reply was lost
    /// has still happened, and this is the only key that finds it again — SAP has no client request id
    /// on an invoice.
    /// </remarks>
    public string IdempotencyField { get; set; } = "U_Van_saleorder";

    /// <summary>Whether <see cref="InvoiceNumberField"/> has been configured.</summary>
    public bool WritesInvoiceNumber => !string.IsNullOrWhiteSpace(InvoiceNumberField);
}

public enum FiscalisationPreflightMode
{
    /// <summary>Only the checks this application can make from the device's cached configuration.</summary>
    Local = 0,

    /// <summary>
    /// The local checks, then the platform's own validator for anything only it can answer.
    /// </summary>
    LocalAndPlatform = 1
}

public class FiscalisationPreflightSettings
{
    public FiscalisationPreflightMode Mode { get; set; } = FiscalisationPreflightMode.LocalAndPlatform;

    /// <summary>
    /// Whether a platform preflight that cannot be reached blocks the submission it was checking.
    /// </summary>
    /// <remarks>
    /// False, deliberately. The preflight is an accuracy measure, not an authorisation step — the
    /// platform validates every submission again on arrival, so a preflight outage degrades the error
    /// message rather than the safety. Blocking on it would convert a preflight outage into a
    /// fiscalisation outage.
    /// </remarks>
    public bool FailClosed { get; set; }
}

public class FiscalDaySettings
{
    /// <summary>Whether this application closes fiscal days and submits offline files at all.</summary>
    public bool AutoCloseEnabled { get; set; }

    /// <summary>
    /// Local wall-clock time the daily close runs at, as <c>HH:mm</c>.
    /// </summary>
    /// <remarks>
    /// After the vans are in and their receipts drained, and far enough before midnight that a retry
    /// still lands on the same day.
    /// </remarks>
    public string CloseAtLocalTime { get; set; } = "20:30";

    /// <summary>
    /// How far through the taxpayer's maximum day length to raise a warning, as a percentage.
    /// </summary>
    /// <remarks>
    /// ZIMRA caps a fiscal day's length and refuses the day if it runs over. The warning has to arrive
    /// with enough of the day left to act on, which is why it is a fraction of the limit rather than a
    /// fixed number of hours — the limit is per taxpayer and is read from the device.
    /// </remarks>
    public int WarnAtPercentOfMaxHrs { get; set; } = 80;

    /// <summary>
    /// How long a day that has used up its attempts waits before it is given another set.
    /// </summary>
    /// <remarks>
    /// The attempt budget exists so a day refused for a standing reason — a counter gap, an expired
    /// certificate — stops writing a log line an hour. But the commonest reason a day burns six attempts is
    /// a platform outage, and that is a condition which clears on its own. Without this, six passes during
    /// one outage parked the day permanently and its receipts never reached ZIMRA. Long enough that the
    /// day is quiet for the rest of a shift, short enough that it comes back the same day.
    /// </remarks>
    public int RetryBudgetCooldownHours { get; set; } = 12;

    /// <summary>
    /// The platform login used for the fiscal-day and offline-file routes, for deployments with one device
    /// or where one account carries every device's claim.
    /// </summary>
    /// <remarks>
    /// Also the fallback for any device with no entry in <see cref="DeviceServiceAccounts"/>.
    /// </remarks>
    public FiscalDayServiceAccountSettings ServiceAccount { get; set; } = new();

    /// <summary>
    /// A platform login per FDMS device id, for a fleet.
    /// </summary>
    /// <remarks>
    /// The platform authorises each admin call against the device the request names, and it reads a
    /// <em>single</em> <c>FdmsDeviceId</c> claim off the bearer token to do it — one account is authorised
    /// for exactly one device. It also refuses password tokens to accounts holding the Admin role, so
    /// "give the service account Admin and be done" is not available. With a single account configured and
    /// a fleet of N handsets, N-1 devices get a bodyless 403 on every call and are eventually parked.
    ///
    /// Keyed by the device id as a string, because that is what a configuration key is:
    /// <c>Fiscalisation:FiscalDay:DeviceServiceAccounts:36189:Username</c>, and
    /// <c>Fiscalisation__FiscalDay__DeviceServiceAccounts__36189__Password</c> as an environment variable.
    /// </remarks>
    public Dictionary<string, FiscalDayServiceAccountSettings> DeviceServiceAccounts { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any credential at all has been supplied, per device or as the default.</summary>
    public bool HasAnyServiceAccount =>
        ServiceAccount.IsConfigured
        || DeviceServiceAccounts.Values.Any(account => account?.IsConfigured == true);

    /// <summary>
    /// The credential to sign in with for one device: its own if it has one, otherwise the default.
    /// </summary>
    /// <remarks>
    /// Never null. An unconfigured account is returned rather than a null, so the caller's one question is
    /// <see cref="FiscalDayServiceAccountSettings.IsConfigured"/> rather than two.
    /// </remarks>
    public FiscalDayServiceAccountSettings ServiceAccountFor(int deviceId)
    {
        var key = deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (DeviceServiceAccounts.TryGetValue(key, out var exact) && exact?.IsConfigured == true)
        {
            return exact;
        }

        // A key written with padding or a sign — " 36189", "+36189" — is the same device, and a credential
        // silently ignored because of a stray space is a 403 nobody can explain.
        foreach (var (configuredKey, account) in DeviceServiceAccounts)
        {
            if (account?.IsConfigured == true
                && int.TryParse(
                    configuredKey,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                && parsed == deviceId)
            {
                return account;
            }
        }

        return ServiceAccount;
    }
}

/// <summary>
/// A named platform login, held because <see cref="FiscalisationSettings.ApiKey"/> cannot reach these routes.
/// </summary>
/// <remarks>
/// The fiscal-day and offline-file endpoints live on the platform's admin route group, which is
/// <c>RequireAuthorization()</c> — it wants a bearer token, and an API-key request arrives there as an
/// anonymous one. <c>POST /auth/token</c> issues that token from a username and password.
///
/// Two constraints on the account, both of which fail as a bare 403 that reads like a wrong password:
/// it must <em>not</em> hold the Admin role, because the platform refuses password tokens to
/// administrators, and it must have no pending one-time password change. It still needs the permissions
/// the routes ask for — fiscal-day management, receipt submission, report reading — which means a
/// non-Admin role carrying them, and, unless it is an Admin, an FDMS device claim: the platform authorises
/// each call against the device it names, and an account with no device claim is refused for every device.
///
/// <para><b>One account authorises one device.</b> The platform's device authorisation reads a single
/// <c>FdmsDeviceId</c> claim, so an account cannot be given the fleet. A deployment with more than one
/// device needs one of these per device, in
/// <see cref="FiscalDaySettings.DeviceServiceAccounts"/>.</para>
///
/// Supply the password through user-secrets locally and the
/// <c>Fiscalisation__FiscalDay__ServiceAccount__Password</c> environment variable in production. It is
/// blank in appsettings.json on purpose.
/// </remarks>
public class FiscalDayServiceAccountSettings
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// How long before a token's stated expiry it is replaced.
    /// </summary>
    /// <remarks>
    /// A close or a submission that fails at the edge of expiry is not free to retry, so the token is
    /// renewed while there is still time for the call it is about to make to finish.
    /// </remarks>
    public int RefreshSkewSeconds { get; set; } = 120;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Identifies this credential to the token cache.
    /// </summary>
    /// <remarks>
    /// The username, so two devices configured with the same account share one token rather than signing
    /// in twice — every sign-in is an audited event on the platform, and a fleet re-authenticating per
    /// device would fill the taxpayer's audit trail with this integration's own noise.
    /// </remarks>
    public string CacheKey => Username.Trim();
}
