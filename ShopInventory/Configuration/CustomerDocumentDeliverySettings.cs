namespace ShopInventory.Configuration;

/// <summary>
/// Sending customers their documents on WhatsApp: the static, cluster-wide half of the settings.
/// </summary>
/// <remarks>
/// <para>
/// Read from appsettings.json and never from a node's own web.config, because <see cref="Enabled"/>
/// decides whether the delivery job is declared, and <c>QuartzStoredJobReconciler</c> deletes every
/// stored job the starting node does not declare. A switch that differed between nodes would have one
/// node delete the other's job at every start.
/// </para>
/// <para>
/// What an administrator changes while the system runs — whether automatic sends are on, which
/// WhatsApp session sends, the daily automatic cap — lives in <c>SystemConfigs</c> instead
/// (<c>CustomerDocumentDeliveryKeys</c>) and is read on every pass, so it reaches every node at once
/// without a deploy.
/// </para>
/// <para>
/// The caps are the defence against WhatsApp restricting the number. OpenWA drives WhatsApp Web, which
/// is not an official business API, and a new number that suddenly sends a few hundred documents to
/// people who never wrote to it looks like spam to WhatsApp whatever the documents say.
/// </para>
/// </remarks>
public sealed class CustomerDocumentDeliverySettings
{
    public const string SectionName = "CustomerDocuments";

    /// <summary>
    /// Whether the delivery job is declared at all. Off, nothing is ever sent and the contact register
    /// still works; on with no session assigned, the job runs and does nothing.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The name of the session that sends documents, by convention. When no session has been chosen
    /// and the gateway has several ready, the one carrying this name is used; with only one ready
    /// session the name does not matter. See <c>CustomerDocumentSession</c>.
    /// </summary>
    public string PreferredSessionName { get; set; } = "customer-documents";

    /// <summary>How often the delivery job looks for documents to send.</summary>
    public int SendIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often SAP is asked for invoices posted since the last look, to queue automatic sends. The
    /// scan is declared only when SAP is switched on as well.
    /// </summary>
    public int InvoiceScanIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// How many DocEntries below the last one seen each scan reads again. SAP hands DocEntries out in
    /// order but two posts can finish out of it, so an invoice can appear just under one already read.
    /// </summary>
    public int InvoiceScanOverlap { get; set; } = 20;

    /// <summary>How many invoice headers one SAP read asks for.</summary>
    public int InvoiceScanPageSize { get; set; } = 200;

    /// <summary>The most reads one scan makes; a backlog after an outage is worked off over several.</summary>
    public int InvoiceScanMaxPagesPerPass { get; set; } = 5;

    /// <summary>
    /// An invoice dated further back than this is not sent automatically: one keyed in late, or a
    /// backlog after a long outage, is no longer news to the customer. A person can still send it.
    /// </summary>
    public int AutoMaxDocumentAgeDays { get; set; } = 7;

    /// <summary>
    /// When automatic sends may go out, as CAT wall-clock times. A person pressing Send is not held to
    /// it: they are looking at the invoice and the customer is usually waiting for it.
    /// </summary>
    public string AutoWindowStartCat { get; set; } = "07:30";

    /// <inheritdoc cref="AutoWindowStartCat"/>
    public string AutoWindowEndCat { get; set; } = "18:00";

    /// <summary>Whether automatic sends go out on a Sunday.</summary>
    public bool AutoSendOnSundays { get; set; }

    /// <summary>The least time between two sends, cluster-wide.</summary>
    public int MinSecondsBetweenSends { get; set; } = 6;

    /// <summary>A random extra wait added to <see cref="MinSecondsBetweenSends"/>, so sends do not tick.</summary>
    public int JitterSeconds { get; set; } = 6;

    /// <summary>The most documents one pass sends.</summary>
    public int MaxSendsPerPass { get; set; } = 8;

    /// <summary>How long one pass may keep sending before it leaves the rest to the next.</summary>
    public int MaxPassMinutes { get; set; } = 4;

    /// <summary>The most documents sent in any hour, manual and automatic together.</summary>
    public int MaxPerHour { get; set; } = 60;

    /// <summary>The most documents sent in a CAT day, manual and automatic together.</summary>
    public int HardMaxPerDay { get; set; } = 300;

    /// <summary>The most documents one number receives in a CAT day.</summary>
    public int MaxPerRecipientPerDay { get; set; } = 20;

    /// <summary>
    /// The most documents one person may send to numbers that are not saved on the customer, per CAT
    /// day. A one-off number is the one a typo or a misunderstanding sends somewhere it should not go.
    /// </summary>
    public int MaxOneOffPerUserPerDay { get; set; } = 30;

    /// <summary>
    /// The most invoices one van rep may send from the handset in a CAT day, to numbers a customer gave
    /// at the sale. A rep's busiest day is about this many sales.
    /// </summary>
    public int MaxVanSaleSendsPerUserPerDay { get; set; } = 80;

    /// <summary>The most "is this number on WhatsApp" checks made in a CAT day.</summary>
    public int MaxNumberChecksPerDay { get; set; } = 50;

    /// <summary>How long a contact's WhatsApp check stays good before it is asked again.</summary>
    public int ContactRecheckDays { get; set; } = 30;

    /// <summary>The most numbers one customer may have on the register.</summary>
    public int MaxContactsPerOwner { get; set; } = 3;

    /// <summary>
    /// How long a document waits for its fiscal receipt before a person is asked to look. A tax
    /// invoice is never sent without the receipt it was filed under.
    /// </summary>
    public int MaxFiscalWaitHours { get; set; } = 24;

    /// <summary>
    /// How long after a document is queued the fiscal device itself is asked about it. The local
    /// records answer nearly every case; the device is the slow last resort.
    /// </summary>
    public int DeviceLookupAfterMinutes { get; set; } = 10;

    /// <summary>How many times a send that provably did not leave is retried before it is failed.</summary>
    public int MaxDispatchAttempts { get; set; } = 5;

    /// <summary>How long a claimed document may sit unsent before another pass may take it.</summary>
    public int StalePreparingMinutes { get; set; } = 10;

    /// <summary>
    /// How long after an ambiguous send OpenWA's own log is asked what happened. Long enough for a send
    /// that was still in flight to have finished one way or the other.
    /// </summary>
    public int UncertainReconcileAfterMinutes { get; set; } = 5;

    /// <summary>The largest document sent. Anything bigger is failed rather than offered to OpenWA.</summary>
    public int MaxDocumentBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>The country code a number written without one is taken to belong to.</summary>
    public string DefaultCountryCode { get; set; } = "+263";

    /// <summary>
    /// The text sent with the document. <c>{customer}</c>, <c>{number}</c>, <c>{date}</c>,
    /// <c>{currency}</c> and <c>{total}</c> are filled in. No links: WhatsApp treats a link from a new
    /// business number as a spam signal, and the PDF already carries the ZIMRA verification link.
    /// </summary>
    public string CaptionTemplate { get; set; } =
        "Good day {customer}. Please find attached Kefalos tax invoice {number} dated {date} for {currency} {total}. Reply to this message with any query.";

    /// <summary>The file name the customer sees. <c>{number}</c> is the document number.</summary>
    public string FileNameTemplate { get; set; } = "Kefalos-Invoice-{number}.pdf";

    /// <summary>
    /// The file name for a sale filed as a 48 mm receipt, which goes out as the till slip rather than
    /// the A4 sheet. The number is the same invoice number either way.
    /// </summary>
    public string ReceiptFileNameTemplate { get; set; } = "Kefalos-Receipt-{number}.pdf";

    /// <summary>How long after an alert the same alert may be raised again.</summary>
    public int AlertCooldownHours { get; set; } = 6;

    /// <summary>How many documents may wait before an administrator is told the queue is backing up.</summary>
    public int BacklogAlertThreshold { get; set; } = 50;
}
