using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The rate SAP books a foreign-currency document at, read the way SAP itself reads it.
/// </summary>
/// <remarks>
/// <para><b>Through <c>SBOBobService_GetCurrencyRate</c>, not ORTT.</b> The function is what the
/// client uses when it prices a document, so a rate it returns is one a ZiG order will be booked at,
/// and a refusal is the same refusal a ZiG order dated that day gets. It also needs no stored
/// <c>SQLQueries</c> entry, and the monitor asks it about forty times an hour.</para>
///
/// <para><b>Rates are keyed per day.</b> Read from production on 2026-11-02: every day up to
/// 30 Nov 2026 answers 30, and 1 Dec answers <c>-4006 "Update the exchange rate"</c>. That is the
/// error a ZiG document dated 1 Dec would fail with, so "no rate" is an answer here, not a
/// failure.</para>
///
/// <para><b>The number's direction is the company's setting.</b> Production quotes indirectly
/// (<c>DirectIndirectRate = tNO</c>): 30 means 1 USD = 30 ZiG. Order 86255 on 2026-11-02 is
/// ZiG 20,906.42 booked as USD 696.88, which is 30.0 exactly.</para>
/// </remarks>
public partial class SAPServiceLayerClient
{
    /// <summary>SAP's answer when no rate is keyed for the currency on that date.</summary>
    internal const int SapNoExchangeRateErrorCode = -4006;

    public async Task<SapCurrencyRateLookup> GetCurrencyRateAsync(
        string currency,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        var json = new JsonObject
        {
            ["Currency"] = currency,
            ["Date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }.ToJsonString();

        var operation = $"read the {currency} rate for {date:yyyy-MM-dd}";

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Post, "SBOBobService_GetCurrencyRate", json),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            // The function's return type is Edm.Double and the Service Layer sends it bare: "30.0".
            if (decimal.TryParse(body.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            {
                return SapCurrencyRateLookup.Found(rate);
            }

            using var document = JsonDocument.Parse(body);
            var value = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("value", out var wrapped)
                ? wrapped
                : document.RootElement;
            return SapCurrencyRateLookup.Found(value.GetDecimal());
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && ReadSapErrorCode(body) == SapNoExchangeRateErrorCode)
        {
            return SapCurrencyRateLookup.NotKeyed(ExtractSAPErrorMessage(body) ?? "Update the exchange rate");
        }

        var message = ExtractSAPErrorMessage(body) ?? (string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body);
        throw new SapRequestRejectedException(operation, response.StatusCode, message);
    }

    public async Task<SapCurrencyRateSettings> GetCurrencyRateSettingsAsync(CancellationToken cancellationToken = default)
    {
        const string operation = "read the company's currency settings";

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Post, "CompanyService_GetAdminInfo", "{}"),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        string? Read(string name) =>
            root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        var localCurrency = Read("LocalCurrency");
        if (string.IsNullOrWhiteSpace(localCurrency))
        {
            throw new SapRequestRejectedException(operation, response.StatusCode, "SAP did not say what its local currency is.");
        }

        return new SapCurrencyRateSettings(
            localCurrency,
            Read("SystemCurrency"),
            // tYES is a direct quote (1 foreign = rate local), tNO an indirect one (1 local = rate foreign).
            IndirectQuotation: !string.Equals(Read("DirectIndirectRate"), "tYES", StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<SapPriceListFactor>> GetPriceListFactorsAsync(CancellationToken cancellationToken = default)
    {
        // 117 lists in production; one page of 500 holds them all. Without the Prefer header SAP
        // answers 20 rows and a nextLink.
        var page = await ReadSapJsonAsync<JsonObject>(
            "PriceLists?$select=PriceListNo,PriceListName,BasePriceList,Factor,DefaultPrimeCurrency,Active&$orderby=PriceListNo",
            "read the price list factors",
            cancellationToken,
            pageSize: 500);

        var lists = new List<SapPriceListFactor>();
        if (page?["value"] is not JsonArray rows)
        {
            return lists;
        }

        foreach (var row in rows.OfType<JsonObject>())
        {
            var number = row["PriceListNo"]?.GetValue<int>() ?? 0;
            lists.Add(new SapPriceListFactor(
                number,
                row["PriceListName"]?.GetValue<string>() ?? $"Price list {number}",
                row["BasePriceList"]?.GetValue<int>() ?? number,
                (decimal)(row["Factor"]?.GetValue<double>() ?? 1d),
                row["DefaultPrimeCurrency"]?.GetValue<string>(),
                string.Equals(row["Active"]?.GetValue<string>(), "tYES", StringComparison.Ordinal)));
        }

        return lists;
    }

    internal static int? ReadSapErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("code", out var code)
                   && code.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
