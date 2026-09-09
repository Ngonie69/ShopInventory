// Drives the real RevmaxFiscalizationService against the live REVMax device.
//
// READ-ONLY BY CONSTRUCTION. A TransactM/TransactMExt POST files a receipt with ZIMRA and cannot be
// withdrawn, so nothing here posts one. What it proves is everything up to that line: that the
// restored client reaches the device, that the service's reconciliation and guard paths give the
// right answers against real responses, and — by printing the exact JSON body — that the payload
// the service would send matches the device's published contract.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

var settings = new RevmaxSettings
{
    Enabled = true,
    BaseUrl = "http://172.16.16.201:8001",
    TimeoutSeconds = 30,
    DefaultCurrency = "USD",
    DefaultBranchName = "Kefalos",
    DefaultRefDeviceId = 22862,
    TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 1, ["O8"] = 1, ["O0"] = 2 },
    DefaultTaxId = 1
};

var tax = new TaxSettings
{
    VatRate = 0.155m,
    RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 0.155m, ["O8"] = 0.155m, ["O0"] = 0m }
};

var client = new RevmaxClient(
    new HttpClient(),
    Options.Create(settings),
    NullLogger<RevmaxClient>.Instance);

var service = new RevmaxFiscalizationService(
    client,
    Options.Create(settings),
    Options.Create(tax),
    Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Revmax }),
    NullLogger<RevmaxFiscalizationService>.Instance);

var passed = 0;
var failed = 0;

void Check(string what, bool ok, string detail)
{
    if (ok) { passed++; Console.WriteLine($"  PASS  {what}\n          {detail}"); }
    else { failed++; Console.WriteLine($"  FAIL  {what}\n          {detail}"); }
}

Console.WriteLine($"REVMax probe against {settings.BaseUrl}  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\n");

// ---------------------------------------------------------------- device reachability
Console.WriteLine("Device");
var card = await client.GetCardDetailsAsync();
Check("GetCardDetails reaches the device",
    card?.Code == "1" && !string.IsNullOrWhiteSpace(card.Data?.TIN),
    $"TIN={card?.Data?.TIN}  device={card?.DeviceID}  serial={card?.DeviceSerialNumber}  {card?.Data?.COMPANYNAME}");

var day = await client.GetDayStatusAsync();
Check("GetDayStatus reports an open fiscal day",
    day?.Code == "1" && day.Data?.FiscalDayStatus == "FiscalDayOpened",
    $"status={day?.Data?.FiscalDayStatus}  day={day?.Data?.LastFiscalDayNo}  lastReceipt={day?.Data?.LastReceiptGlobalNo}");

var licence = await client.GetLicenseAsync();
Check("GetLicense returns an active licence", licence?.Code == "1", $"{licence?.Data}");

// ---------------------------------------------------------------- guards that must not sign twice
Console.WriteLine("\nDuplicate guards");
var unknown = $"PROBE-{Guid.NewGuid():N}"[..20];

var isFiscalised = await service.IsInvoiceFiscalizedAsync(unknown);
Check("IsInvoiceFiscalizedAsync is false for an invoice the device has never seen",
    !isFiscalised, $"reference={unknown} -> {isFiscalised}");

var found = await service.FindPreSapReceiptAsync(unknown);
Check("FindPreSapReceiptAsync returns null on a positive 'Invoice not Found'",
    found is null, "null means the device answered and holds nothing, so signing is safe");

// The same call must THROW rather than answer null when the device cannot be asked at all —
// otherwise "I could not check" reads as "there is nothing there" and a second receipt gets signed.
var unreachable = new RevmaxFiscalizationService(
    new RevmaxClient(
        new HttpClient(),
        Options.Create(new RevmaxSettings
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:9",
            TimeoutSeconds = 2,
            MaxRetries = 0
        }),
        NullLogger<RevmaxClient>.Instance),
    Options.Create(settings),
    Options.Create(tax),
    Options.Create(new FiscalisationSettings()),
    NullLogger<RevmaxFiscalizationService>.Instance);

try
{
    await unreachable.FindPreSapReceiptAsync(unknown);
    Check("FindPreSapReceiptAsync throws when the device cannot be asked", false,
        "it returned instead of throwing — a duplicate receipt is reachable from here");
}
catch (InvalidOperationException ex)
{
    Check("FindPreSapReceiptAsync throws when the device cannot be asked", true,
        ex.Message.Split('.')[0]);
}

// ---------------------------------------------------------------- credit note linkage
Console.WriteLine("\nCredit notes");
var creditNote = BuildDocument(docNum: 999999, total: -115.50m, vat: -15.50m);
var refused = await service.FiscalizeCreditNoteAsync(creditNote, unknown);
Check("A credit note whose original the device does not hold is refused, not filed unlinked",
    !refused.Success && refused.ErrorCode == "ORIGINAL_RECEIPT_NOT_FOUND",
    refused.Message ?? "(no message)");

// ---------------------------------------------------------------- read-back
Console.WriteLine("\nRead-back");
// RevmaxFiscalReceiptReader is internal to ShopInventory, so it is built reflectively here rather
// than widening production visibility for the sake of a probe.
var readerType = typeof(RevmaxFiscalizationService).Assembly
    .GetType("ShopInventory.Common.Fiscalization.RevmaxFiscalReceiptReader")!;
var reader = (IFiscalReceiptReader)Activator.CreateInstance(readerType, client, settings)!;
var snapshot = await reader.TryLookupAsync(999999, ReceiptType.FiscalInvoice, NullLogger.Instance, default);
Check("Read-back distinguishes 'not fiscalised' from 'could not ask'",
    snapshot is { IsFiscalised: false },
    snapshot is null ? "null == lookup failed (wrong here)" : "snapshot with IsFiscalised=false (correct)");

// ---------------------------------------------------------------- the payload, unsent
Console.WriteLine("\nPayload the service would send (NOT sent)");
var invoice = BuildDocument(docNum: 123456, total: 115.50m, vat: 15.50m);
var body = BuildBodyWithoutSending(service, invoice);
Console.WriteLine(body);

var parsed = JsonDocument.Parse(body).RootElement;
Check("Amounts and quantities serialise as strings, as the contract requires",
    parsed.GetProperty("InvoiceAmount").ValueKind == JsonValueKind.String
    && parsed.GetProperty("ItemsXml")[0].GetProperty("QTY").ValueKind == JsonValueKind.String,
    $"InvoiceAmount={parsed.GetProperty("InvoiceAmount")}  QTY={parsed.GetProperty("ItemsXml")[0].GetProperty("QTY")}");

Check("Istatus marks an ordinary invoice",
    parsed.GetProperty("Istatus").GetString() == "01",
    $"Istatus={parsed.GetProperty("Istatus").GetString()}");

Check("Zero-rated and standard-rated lines carry different tax ids and rates",
    parsed.GetProperty("ItemsXml")[0].GetProperty("TAXR").GetString() == "15.5"
    && parsed.GetProperty("ItemsXml")[1].GetProperty("TAXR").GetString() == "0"
    && parsed.GetProperty("ItemsXml")[1].GetProperty("TAX").GetString() == "2",
    $"line0 TAX={parsed.GetProperty("ItemsXml")[0].GetProperty("TAX")} TAXR={parsed.GetProperty("ItemsXml")[0].GetProperty("TAXR")}; "
    + $"line1 TAX={parsed.GetProperty("ItemsXml")[1].GetProperty("TAX")} TAXR={parsed.GetProperty("ItemsXml")[1].GetProperty("TAXR")}");

var preSap = await unreachable.FiscalizePreSapInvoiceAsync(invoice, "4021");
Check("A bare numeric pre-SAP reference is lifted out of the SAP DocNum namespace",
    preSap.InvoiceNumber == "SI-4021",
    $"4021 -> {preSap.InvoiceNumber}");

// ---------------------------------------------------------------- a real SAP invoice, end to end
// The mapping chain is where the money goes wrong, so this runs a real SAP document through the real
// Invoice -> InvoiceLine.ToDto() -> RevmaxFiscalizationService path and checks the declared total
// against SAP's own DocTotal. Still nothing is sent.
var fixture = Path.Combine(AppContext.BaseDirectory, "sample-invoice-769617.json");

if (File.Exists(fixture))
{
    Console.WriteLine("\nA real SAP invoice through the real mapping (769617)");

    var sap = JsonSerializer.Deserialize<ShopInventory.Models.Invoice>(
        File.ReadAllText(fixture),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    var mapped = new InvoiceDto
    {
        DocEntry = sap.DocEntry,
        DocNum = sap.DocNum,
        CardCode = sap.CardCode,
        CardName = sap.CardName,
        DocCurrency = sap.DocCurrency,
        DocTotal = sap.DocTotal,
        VatSum = sap.VatSum,
        Lines = sap.DocumentLines?.Select(l => l.ToDto()).ToList()
    };

    var realBody = BuildBodyWithoutSending(service, mapped);
    var realParsed = JsonDocument.Parse(realBody).RootElement;

    var declared = realParsed.GetProperty("ItemsXml").EnumerateArray()
        .Sum(item => decimal.Parse(
            item.GetProperty("AMT").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));

    Check("Declared line amounts reconcile with SAP's document total",
        Math.Abs(declared - sap.DocTotal) <= 0.05m,
        $"lines sum to {declared:0.00} against SAP DocTotal {sap.DocTotal:0.00} "
        + $"(difference {declared - sap.DocTotal:0.00})");

    Check("The VAT group is read, so no line falls silently to the standard-rated default",
        realParsed.GetProperty("ItemsXml").EnumerateArray()
            .All(item => item.GetProperty("TAXR").GetString() == "15.5"),
        "every line on this invoice is VatGroup O01, so all should declare 15.5");

    Console.WriteLine("\n  the 71-unit line as it would be declared:");
    Console.WriteLine("  " + realParsed.GetProperty("ItemsXml")[3].GetRawText());
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static InvoiceDto BuildDocument(int docNum, decimal total, decimal vat) => new()
{
    DocEntry = docNum,
    DocNum = docNum,
    DocDate = DateTime.Today.ToString("yyyy-MM-dd"),
    CardCode = "SPA059",
    CardName = "Probe Customer",
    DocCurrency = "USD",
    DocTotal = total,
    VatSum = vat,
    Comments = "Probe document — never sent",
    Lines =
    [
        new InvoiceLineDto
        {
            LineNum = 0, ItemCode = "CHE011", ItemDescription = "Feta 1kg",
            Quantity = 2m, UnitPrice = 43.29m, GrossPrice = 50.00m, LineTotal = 86.58m, TaxCode = "O01"
        },
        new InvoiceLineDto
        {
            LineNum = 1, ItemCode = "NRI049", ItemDescription = "Zero rated line",
            Quantity = 1m, UnitPrice = 15.50m, GrossPrice = 15.50m, LineTotal = 15.50m, TaxCode = "O0"
        }
    ]
};

// Reaches the request the service builds without letting it leave the process: FiscalizeInvoiceAsync
// puts the serialised body on RawRequestJson, and pointing the client at a closed port means the POST
// fails before the device sees it.
static string BuildBodyWithoutSending(RevmaxFiscalizationService live, InvoiceDto invoice)
{
    var offline = new RevmaxFiscalizationService(
        new RevmaxClient(
            new HttpClient(),
            Options.Create(new RevmaxSettings
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:9",
                TimeoutSeconds = 2,
                MaxRetries = 0,
                DefaultCurrency = "USD",
                DefaultBranchName = "Kefalos",
                TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 1, ["O8"] = 1, ["O0"] = 2 },
                DefaultTaxId = 1
            }),
            NullLogger<RevmaxClient>.Instance),
        Options.Create(new RevmaxSettings
        {
            Enabled = true,
            DefaultCurrency = "USD",
            DefaultBranchName = "Kefalos",
            TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 1, ["O8"] = 1, ["O0"] = 2 },
            DefaultTaxId = 1
        }),
        Options.Create(new TaxSettings
        {
            VatRate = 0.155m,
            RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 0.155m, ["O0"] = 0m }
        }),
        Options.Create(new FiscalisationSettings()),
        NullLogger<RevmaxFiscalizationService>.Instance);

    var result = offline.FiscalizeInvoiceAsync(invoice).GetAwaiter().GetResult();

    return JsonSerializer.Serialize(
        JsonDocument.Parse(result.RawRequestJson!).RootElement,
        new JsonSerializerOptions { WriteIndented = true });
}
