using ErrorOr;
using MediatR;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;

public sealed class GetVanSalesInvoicesHandler(ApplicationDbContext db)
    : IRequestHandler<GetVanSalesInvoicesQuery, ErrorOr<VanSalesInvoicesResult>>
{
    /// <summary>A period long enough to answer any question about a month, short enough to read in one go.</summary>
    internal const int MaxDays = 93;

    internal const int MaxPageSize = 200;

    public async Task<ErrorOr<VanSalesInvoicesResult>> Handle(
        GetVanSalesInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        var from = request.FromDate.Date;
        var to = request.ToDate.Date;

        if (to < from)
        {
            return Error.Validation("VanSalesDocuments.InvalidRange", "The end date is before the start date.");
        }

        if ((to - from).TotalDays >= MaxDays)
        {
            return Error.Validation(
                "VanSalesDocuments.RangeTooLong",
                $"Choose a period of at most {MaxDays} days.");
        }

        if (request.State is not null && !VanSalesDocumentStates.IsKnown(request.State))
        {
            return Error.Validation("VanSalesDocuments.UnknownState", $"'{request.State}' is not a state.");
        }

        if (request.Channel is not null && NormaliseChannel(request.Channel) is null)
        {
            return Error.Validation("VanSalesDocuments.UnknownChannel", $"'{request.Channel}' is not a channel.");
        }

        var channel = NormaliseChannel(request.Channel);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var records = await VanSalesInvoiceReader.LoadAsync(db, from, to, reference: null, cancellationToken);

        // Rep and search narrow the period, then channel; the state counts are taken after them and before the
        // state filter, so each count says what choosing that state would show. The channel counts are taken
        // before the channel filter, for the same reason.
        var searched = records
            .Select(r => r.Row)
            .Where(row => request.RepUserId is null || row.RepUserId == request.RepUserId)
            .Where(row => Matches(row, request.Search))
            .ToList();

        var rows = searched
            .Where(row => channel is null || row.Channel == channel)
            .ToList();

        var counts = new VanSalesInvoiceCounts(
            rows.Count,
            rows.Count(r => r.State == VanSalesDocumentStates.Complete),
            rows.Count(r => r.State == VanSalesDocumentStates.AwaitingSap),
            rows.Count(r => r.State == VanSalesDocumentStates.NotFiscalised),
            rows.Count(r => r.State == VanSalesDocumentStates.InProgress),
            rows.Count(r => r.State == VanSalesDocumentStates.NeedsAttention));

        var filtered = rows
            .Where(row => request.State is null || row.State == request.State)
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenBy(row => row.Reference, StringComparer.Ordinal)
            .ToList();

        var reps = records
            .Where(r => r.Row.RepUserId is not null)
            .GroupBy(r => r.Row.RepUserId!.Value)
            .Select(g => new VanSalesRepOption(g.Key, g.First().Row.RepName ?? "Unknown rep"))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanSalesInvoicesResult(
            from,
            to,
            page,
            pageSize,
            filtered.Count,
            counts,
            filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            reps,
            Summarise(searched, rows));
    }

    /// <summary>The channel as the rows spell it, or null when it is none.</summary>
    internal static string? NormaliseChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "online" => "Online",
        "offline" => "Offline",
        _ => null
    };

    /// <param name="beforeChannel">The rows the channel counts are taken over.</param>
    /// <param name="rows">The rows everything else is taken over.</param>
    internal static VanSalesInvoiceSummary Summarise(
        IReadOnlyCollection<VanSalesInvoiceRow> beforeChannel,
        IReadOnlyCollection<VanSalesInvoiceRow> rows)
    {
        var notInSap = rows.Where(row => row.SapDocNum is null).ToList();

        var byVan = notInSap
            .GroupBy(row => (Van: string.IsNullOrWhiteSpace(row.WarehouseCode) ? "No van" : row.WarehouseCode!, row.Currency))
            .Select(g =>
            {
                var reps = g.Select(row => row.RepName).Distinct().ToList();

                return new VanSalesVanBacklog(
                    g.Key.Van,
                    reps.Count == 1 ? reps[0] : null,
                    g.Key.Currency,
                    g.Sum(row => row.Amount),
                    g.Count());
            })
            .OrderByDescending(van => van.Amount)
            .ThenBy(van => van.WarehouseCode, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return new VanSalesInvoiceSummary(
            beforeChannel.Count(row => row.Channel == "Online"),
            beforeChannel.Count(row => row.Channel == "Offline"),
            TotalsByCurrency(rows),
            notInSap.Count,
            TotalsByCurrency(notInSap),
            byVan);
    }

    private static List<VanSalesMoneyTotal> TotalsByCurrency(IEnumerable<VanSalesInvoiceRow> rows) =>
        rows
            .GroupBy(row => row.Currency)
            .Select(g => new VanSalesMoneyTotal(
                g.Key,
                g.Sum(row => row.Amount),
                g.Sum(row => row.VatAmount ?? 0m),
                g.Count(),
                g.Count(row => !row.AmountIncludesVat)))
            .OrderByDescending(total => total.Count)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();

    internal static bool Matches(VanSalesInvoiceRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();

        return Contains(row.Reference, term)
               || Contains(row.CustomerName, term)
               || Contains(row.CustomerCode, term)
               || Contains(row.RepName, term)
               || Contains(row.FiscalReceiptNumber, term)
               || Contains(row.SapDocNum?.ToString(), term);
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
