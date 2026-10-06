using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

public sealed class GetDesktopSalesAnalysisHandler(
    HttpClient httpClient,
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<GetDesktopSalesAnalysisHandler> logger
) : IRequestHandler<GetDesktopSalesAnalysisQuery, ErrorOr<DesktopSalesAnalysisResult>>
{
    public async Task<ErrorOr<DesktopSalesAnalysisResult>> Handle(
        GetDesktopSalesAnalysisQuery request,
        CancellationToken cancellationToken)
    {
        var queryParts = new List<string>();

        if (request.FromDate.HasValue)
        {
            queryParts.Add($"fromDate={request.FromDate.Value:yyyy-MM-dd}");
        }

        if (request.ToDate.HasValue)
        {
            queryParts.Add($"toDate={request.ToDate.Value:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(request.WarehouseCode))
        {
            queryParts.Add($"warehouseCode={Uri.EscapeDataString(request.WarehouseCode.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(request.PaymentMethod))
        {
            queryParts.Add($"paymentMethod={Uri.EscapeDataString(request.PaymentMethod.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(request.SourceSystem) && !request.Vans)
        {
            queryParts.Add($"sourceSystem={Uri.EscapeDataString(request.SourceSystem.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(request.CardCode) && !request.Vans)
        {
            queryParts.Add($"cardCode={Uri.EscapeDataString(request.CardCode.Trim())}");
        }

        var path = request.Vans ? "api/van-sales/sales-analysis" : "api/DesktopIntegration/sales/analysis";
        var url = queryParts.Count == 0 ? path : $"{path}?{string.Join("&", queryParts)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadProblemDetailAsync(response, cancellationToken);

                logger.LogWarning(
                    "Failed to load the desktop sales analysis from {Url}. Status code: {StatusCode}. Detail: {Detail}",
                    url,
                    (int)response.StatusCode,
                    detail);

                // The API's own words where it gave some. A refused warehouse and a period that is too
                // long are both things the operator can act on, and "failed to load" is not.
                return Errors.Report.LoadDesktopSalesAnalysisFailed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        "Your session could not access the desktop sales analysis. Refresh and try again.",
                    HttpStatusCode.Forbidden or HttpStatusCode.BadRequest when detail is not null => detail!,
                    HttpStatusCode.Forbidden => "This account is not permitted to read these sales.",
                    _ => "Failed to load the desktop sales analysis."
                });
            }

            var result = await response.Content.ReadFromJsonAsync<DesktopSalesAnalysisResult>(
                cancellationToken: cancellationToken);

            if (result is null)
            {
                return Errors.Report.LoadDesktopSalesAnalysisFailed("Failed to load the desktop sales analysis.");
            }

            await NameRowsAsync(result, cancellationToken);

            logger.LogInformation(
                "Loaded desktop sales analysis from {FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}: {CurrencyCount} currency section(s), {SalesCount} sale(s)",
                result.FromDate,
                result.ToDate,
                result.Currencies.Count,
                result.Currencies.Sum(currency => currency.SalesCount));

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the desktop sales analysis from {Url}", url);
            return Errors.Report.LoadDesktopSalesAnalysisFailed("Failed to load the desktop sales analysis.");
        }
    }

    /// <summary>
    /// Labels each shop or van by its warehouse name, and each van account by its business partner name. The
    /// API knows only the codes — neither sales table holds those names — and the Web's caches do.
    /// </summary>
    /// <remarks>
    /// Read without the usual active-only filters: a retired warehouse or a frozen van account still sold in
    /// the period, and its name is no less its name. A code a cache does not hold keeps the code as its label.
    /// The page keys on <c>Key</c>, never <c>Label</c>, so naming here changes only what is written out.
    /// </remarks>
    private async Task NameRowsAsync(DesktopSalesAnalysisResult result, CancellationToken cancellationToken)
    {
        var warehouseRows = result.Currencies.SelectMany(section => section.ByWarehouse).ToList();
        var accountRows = result.Currencies.SelectMany(section => section.ByVanAccount).ToList();
        var warehouseCodes = Codes(warehouseRows);
        var accountCodes = Codes(accountRows);

        if (warehouseCodes.Count == 0 && accountCodes.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (warehouseCodes.Count > 0)
            {
                Name(warehouseRows, (await db.CachedWarehouses
                        .AsNoTracking()
                        .Where(warehouse => warehouseCodes.Contains(warehouse.WarehouseCode))
                        .Select(warehouse => new { warehouse.WarehouseCode, warehouse.WarehouseName })
                        .ToListAsync(cancellationToken))
                    .Select(warehouse => (warehouse.WarehouseCode, warehouse.WarehouseName)));
            }

            if (accountCodes.Count > 0)
            {
                Name(accountRows, (await db.CachedBusinessPartners
                        .AsNoTracking()
                        .Where(partner => accountCodes.Contains(partner.CardCode))
                        .Select(partner => new { partner.CardCode, partner.CardName })
                        .ToListAsync(cancellationToken))
                    .Select(partner => (partner.CardCode, partner.CardName)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The figures stand without the names; the codes are still on every row.
            logger.LogWarning(ex, "Could not name the shops, vans or van accounts in the sales analysis");
        }

        static List<string> Codes(List<DesktopSalesBreakdownRow> rows) =>
            rows.Select(row => row.Key).Where(code => code != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static void Name(List<DesktopSalesBreakdownRow> rows, IEnumerable<(string Code, string? Name)> cached)
        {
            var names = cached
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name!.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (names.TryGetValue(row.Key, out var name))
                {
                    row.Label = name;
                }
            }
        }
    }

    /// <summary>
    /// The first message a problem response carries — a validation message, else its detail.
    /// </summary>
    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var message in field.Value.EnumerateArray())
                    {
                        if (message.ValueKind == JsonValueKind.String)
                        {
                            return message.GetString();
                        }
                    }
                }
            }

            return root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
