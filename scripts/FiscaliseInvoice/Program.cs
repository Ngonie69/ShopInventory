// Fiscalises one SAP invoice through the real RevmaxFiscalizationService.
//
// DRY RUN BY DEFAULT. It builds the payload, prints it, and reconciles the declared lines against
// SAP's own DocTotal without sending anything. Filing requires --post, spelled out, because a receipt
// at ZIMRA cannot be withdrawn — only reversed with a manual credit note. This default exists because
// the opposite one bit: a tool that posted unless told otherwise filed a receipt when the flag that
// was supposed to stop it silently failed to apply.
//
//   dotnet run -- 771149                      read SAP, build, print, send nothing
//   dotnet run -- 771149 --post               file it
//   dotnet run -- 771149 --session <B1SESSION>
//
// Without --session it reads a local fixture named sap<docNum>.json instead of calling SAP.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;

var post = args.Any(a => a.Equals("--post", StringComparison.OrdinalIgnoreCase));
var session = ValueOf("--session");
var positional = args.Where(a => !a.StartsWith('-')).ToArray();

if (positional.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run -- <docNum> [--post] [--session <B1SESSION>]");
    return 64;
}

var docNum = positional[0];

var settings = new RevmaxSettings
{
    Enabled = true,
    BaseUrl = "http://172.16.16.201:8001",
    TimeoutSeconds = 90,
    DefaultCurrency = "USD",
    DefaultBranchName = "Kefalos",
    DefaultRefDeviceId = 22862,
    TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 1, ["O8"] = 1, ["O0"] = 2 },
    DefaultTaxId = 1
};

var tax = new TaxSettings
{
    VatRate = 0.155m,
    RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["O01"] = 0.155m,
        ["O8"] = 0.155m,
        ["O0"] = 0m
    }
};

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));

var client = new RevmaxClient(
    new HttpClient(),
    Options.Create(settings),
    loggerFactory.CreateLogger<RevmaxClient>());

var invoice = await LoadInvoiceAsync(docNum, session);

Console.WriteLine($"DocNum {invoice.DocNum}  {invoice.CardCode}  {invoice.CardName}");
Console.WriteLine($"  {invoice.DocCurrency} {invoice.DocTotal:0.00} (VAT {invoice.VatSum:0.00}), "
                  + $"{invoice.Lines?.Count ?? 0} lines");
Console.WriteLine($"  VAT groups: {string.Join(", ",
    (invoice.Lines ?? []).Select(l => l.VatGroup ?? l.TaxCode ?? "(none)").Distinct())}\n");

// Read first, always — in both modes. In dry run it tells you whether posting would even be allowed;
// before a post it is the guard, and the two are not atomic, so it is re-read here rather than
// trusted from earlier in the session.
var existing = await client.GetInvoiceAsync(docNum);

if (existing is null)
{
    Console.WriteLine("The device gave no answer when asked what it holds for this invoice. Stopping: "
                      + "an unanswerable check must never be read as 'nothing is there'.");
    return 2;
}

if (existing.Success)
{
    Console.WriteLine($"Already fiscalised: receipt {existing.Data?.ReceiptGlobalNo}, "
                      + $"total {existing.Data?.ReceiptTotal:0.00}, {existing.Data?.ReceiptDate}. "
                      + "Nothing to do.");
    return 3;
}

Console.WriteLine($"Device holds nothing for {docNum} ({existing.Message}).\n");

var service = BuildService(post ? client : OfflineClient());
var result = await service.FiscalizeInvoiceAsync(invoice);

if (result.RawRequestJson is not null)
{
    var body = JsonDocument.Parse(result.RawRequestJson).RootElement;

    Console.WriteLine(post ? "Payload sent:" : "DRY RUN — payload built, nothing sent:");
    Console.WriteLine(JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));

    var declared = body.GetProperty("ItemsXml").EnumerateArray()
        .Sum(i => decimal.Parse(i.GetProperty("AMT").GetString()!, CultureInfo.InvariantCulture));

    var drift = declared - invoice.DocTotal;
    Console.WriteLine($"\nDeclared lines sum to {declared:0.00} against SAP DocTotal "
                      + $"{invoice.DocTotal:0.00} (drift {drift:+0.00;-0.00;0.00}).");

    if (Math.Abs(drift) > 0.10m)
    {
        Console.WriteLine("  That is too large to be rounding. Check the price basis before posting.");
    }
}

if (!post)
{
    Console.WriteLine("\nNothing was filed. Re-run with --post to file it.");
    return 0;
}

Console.WriteLine("\n---------------- RESULT ----------------");
Console.WriteLine($"Success                : {result.Success}");
Console.WriteLine($"RequiresReconciliation : {result.RequiresReconciliation}");
Console.WriteLine($"Message                : {result.Message}");
Console.WriteLine($"ErrorCode              : {result.ErrorCode ?? "-"}");
Console.WriteLine($"FiscalDayNo            : {result.FiscalDayNo ?? "-"}");
Console.WriteLine($"DeviceSerial           : {result.DeviceSerial ?? "-"}");
Console.WriteLine($"VerificationCode       : {result.VerificationCode ?? "-"}");
Console.WriteLine($"QRCode                 : {result.QRCode ?? "-"}");

if (!result.Success)
{
    return 1;
}

// The response carries no receipt number, so read it back rather than parsing it out of the QR.
var filed = await client.GetInvoiceAsync(docNum);
Console.WriteLine($"\nRead back: receipt {filed?.Data?.ReceiptGlobalNo}, counter "
                  + $"{filed?.Data?.ReceiptCounter}, total {filed?.Data?.ReceiptTotal:0.00}, "
                  + $"{filed?.Data?.ReceiptDate}");
return 0;

string? ValueOf(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

RevmaxFiscalizationService BuildService(IRevmaxClient revmax) =>
    new(revmax,
        Options.Create(settings),
        Options.Create(tax),
        Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Revmax }),
        post
            ? loggerFactory.CreateLogger<RevmaxFiscalizationService>()
            : Microsoft.Extensions.Logging.Abstractions.NullLogger<RevmaxFiscalizationService>.Instance);

// A closed port. The service builds the whole payload and records it on RawRequestJson before the
// POST fails, which is what makes a dry run exercise the real code rather than a copy of it.
IRevmaxClient OfflineClient() =>
    new RevmaxClient(
        new HttpClient(),
        Options.Create(new RevmaxSettings
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:9",
            TimeoutSeconds = 2,
            MaxRetries = 0,
            DefaultCurrency = settings.DefaultCurrency,
            DefaultBranchName = settings.DefaultBranchName,
            DefaultRefDeviceId = settings.DefaultRefDeviceId,
            TaxIdMappings = settings.TaxIdMappings,
            DefaultTaxId = settings.DefaultTaxId
        }),
        // Silent: the POST to a closed port is how the dry run works, so its failure is the expected
        // path and logging a stack trace for it buries the payload the operator is here to read.
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RevmaxClient>.Instance);

async Task<InvoiceDto> LoadInvoiceAsync(string number, string? b1Session)
{
    ShopInventory.Models.Invoice sap;

    if (string.IsNullOrWhiteSpace(b1Session))
    {
        var path = $"sap{number}.json";

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"No --session given and no local fixture at {path}.");
        }

        sap = JsonSerializer.Deserialize<ShopInventory.Models.Invoice>(
            await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    else
    {
        // The Service Layer presents a self-signed certificate on the LAN.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var sapHttp = new HttpClient(handler);
        sapHttp.DefaultRequestHeaders.Add("Cookie", $"B1SESSION={b1Session}");

        var list = await sapHttp.GetStringAsync(
            $"https://10.10.10.6:50000/b1s/v1/Invoices?$filter=DocNum eq {number}&$select=DocEntry");

        var docEntry = JsonDocument.Parse(list).RootElement
            .GetProperty("value").EnumerateArray().FirstOrDefault()
            .GetProperty("DocEntry").GetInt32();

        var document = await sapHttp.GetStringAsync(
            $"https://10.10.10.6:50000/b1s/v1/Invoices({docEntry})");

        sap = JsonSerializer.Deserialize<ShopInventory.Models.Invoice>(
            document, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    return new InvoiceDto
    {
        DocEntry = sap.DocEntry,
        DocNum = sap.DocNum,
        CardCode = sap.CardCode,
        CardName = sap.CardName,
        DocCurrency = sap.DocCurrency,
        DocTotal = sap.DocTotal,
        VatSum = sap.VatSum,
        Comments = sap.Comments,
        Lines = sap.DocumentLines?.Select(l => l.ToDto()).ToList()
    };
}
