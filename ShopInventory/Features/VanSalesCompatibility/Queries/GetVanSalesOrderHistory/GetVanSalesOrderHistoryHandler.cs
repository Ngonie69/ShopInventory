
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;

public sealed class GetVanSalesOrderHistoryHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    ILogger<GetVanSalesOrderHistoryHandler> logger
) : IRequestHandler<GetVanSalesOrderHistoryQuery, ErrorOr<List<VanSalesLegacyOrderDto>>>
{
    /// <summary>
    /// How far back an invoice history reaches when the handset names no dates.
    /// </summary>
    /// <remarks>
    /// The handset always sends both bounds, so this covers the callers that do not. It is a ceiling
    /// rather than a default worth tuning: the read used to bound itself on the span of the rep's own
    /// fiscal receipts, and with those no longer read first an open window would ask SAP for
    /// everything.
    /// </remarks>
    private const int DefaultInvoiceHistoryDays = 60;

    /// <summary>
    /// The most invoices read for one customer code in one window.
    /// </summary>
    /// <remarks>
    /// A van raises a few dozen invoices a day against a single business partner, so a fortnight is
    /// comfortably inside this and a year is not. Hitting it is logged rather than silently trimmed —
    /// a shortened list and a quiet fortnight look identical on a handset.
    ///
    /// <para>Kept modest because this read asks SAP to expand each invoice's lines, which it pages in
    /// hundreds. The ceiling is a backstop against an open window, not a page size.</para>
    /// </remarks>
    private const int MaxInvoicesPerCustomer = 500;

    public async Task<ErrorOr<List<VanSalesLegacyOrderDto>>> Handle(
        GetVanSalesOrderHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var normalizedType = query.Request.Type?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedType) &&
            !string.Equals(normalizedType, "SO", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedType, "INV", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "The van sales history endpoint supports only invoice and sales-order filters.");
        }

        var includeSalesOrders = string.IsNullOrWhiteSpace(normalizedType) ||
            string.Equals(normalizedType, "SO", StringComparison.OrdinalIgnoreCase);
        var includeInvoices = string.IsNullOrWhiteSpace(normalizedType) ||
            string.Equals(normalizedType, "INV", StringComparison.OrdinalIgnoreCase);

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var window = VanSalesLegacyDateWindow.Parse(query.Request.StartDate, query.Request.EndDate);

        var history = new List<VanSalesLegacyOrderDto>();

        if (includeSalesOrders)
        {
            history.AddRange(await GetSalesOrderHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                window,
                cancellationToken));
        }

        if (includeInvoices)
        {
            history.AddRange(await GetInvoiceHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                window,
                cancellationToken));
        }

        return history
            .OrderByDescending(order => VanSalesCompatibilityMapper.ParseLegacyDate(order.Timestamps.CreateDate) ?? DateTime.MinValue)
            .ThenByDescending(order => order.Id)
            .ToList();
    }

    private async Task<List<VanSalesLegacyOrderDto>> GetSalesOrderHistoryAsync(
        Guid userId,
        IReadOnlyCollection<string> effectiveCustomerCodes,
        VanSalesLegacyDateWindow window,
        CancellationToken cancellationToken)
    {
        var salesOrdersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile && order.CreatedByUserId == userId);

        if (effectiveCustomerCodes.Count > 0)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => effectiveCustomerCodes.Contains(order.CardCode));
        }

        // OrderDate is timestamptz, so the trading days the handset asked for are compared as the UTC
        // instants they cover rather than as bare dates.
        if (window.FromUtc is { } fromUtc)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate >= fromUtc);
        }

        if (window.ToUtcExclusive is { } toUtcExclusive)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate < toUtcExclusive);
        }

        var orders = await salesOrdersQuery
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.Id)
            .Select(order => new SalesOrderDto
            {
                Id = order.Id,
                SAPDocEntry = order.SAPDocEntry,
                SAPDocNum = order.SAPDocNum,
                OrderNumber = order.OrderNumber,
                OrderDate = order.OrderDate,
                DeliveryDate = order.DeliveryDate,
                CardCode = order.CardCode,
                CardName = order.CardName,
                Currency = order.Currency,
                TaxAmount = order.TaxAmount,
                DocTotal = order.DocTotal,
                CreatedAt = order.CreatedAt,
                ApprovedDate = order.ApprovedDate,
                InvoiceSapDocNum = order.Invoice != null ? order.Invoice.SAPDocNum : null,
                Status = order.Status,
                Lines = order.Lines
                    .OrderBy(line => line.LineNum)
                    .Select(line => new SalesOrderLineDto
                    {
                        Id = line.Id,
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return orders
            .Select(VanSalesCompatibilityMapper.MapLegacySalesOrder)
            .ToList();
    }

    /// <summary>
    /// The invoices standing in SAP against the codes this account is scoped to.
    /// </summary>
    /// <remarks>
    /// <para>SAP is the record, and the fiscal transaction log is a note attached to it. It used to be
    /// the other way round: the invoices were inner-joined onto this rep's own rows in
    /// <c>DesktopFiscalTransactions</c>, so an invoice appeared only if <em>this</em> account had
    /// fiscalised it. Every other invoice against the same shop was invisible — one raised at the
    /// depot, one raised by whoever had the handset yesterday, one raised before the account existed.</para>
    ///
    /// <para>The join did not merely narrow the list, it very nearly emptied it. A van sale is signed
    /// on the handset and uploaded, and the invoice is cut at end of day by
    /// <c>ConsolidateDailySalesHandler</c>, which records its fiscal transaction through
    /// <c>InvoiceFiscalTransactionSync.RecordConsolidatedInvoiceAsync</c> — and that passes no user, so
    /// the row carries a null <c>CreatedByUserId</c> and matched no rep. Only the server-fiscalised
    /// fallback path, which is the exception rather than the rule, ever showed up here.</para>
    ///
    /// <para><b>An empty scope returns nothing, never everything.</b> The customer filter used to be
    /// written as "no codes means no filter", which was survivable only because the fiscal join was
    /// doing the real narrowing behind it. With that gone, the same line would hand a handset in a van
    /// every invoice the company raised in the window.</para>
    /// </remarks>
    private async Task<List<VanSalesLegacyOrderDto>> GetInvoiceHistoryAsync(
        Guid userId,
        IReadOnlyCollection<string> effectiveCustomerCodes,
        VanSalesLegacyDateWindow window,
        CancellationToken cancellationToken)
    {
        if (effectiveCustomerCodes.Count == 0)
        {
            logger.LogWarning(
                "Van sales user {UserId} has no customer scope, so no invoice history can be read for them",
                userId);

            return new List<VanSalesLegacyOrderDto>();
        }

        // SAP filters DocDate as a calendar date in its own CAT terms, so this half takes the trading
        // days themselves rather than the instants they cover. An unasked-for bound falls back to a
        // bounded recent window: it used to fall back to the span of the rep's own fiscal receipts,
        // which is no longer read first and may not exist at all.
        var todayCat = AuditService.ToCAT(DateTime.UtcNow).Date;
        var sapFromDate = window.FromDate ?? todayCat.AddDays(-DefaultInvoiceHistoryDays);
        var sapToDate = window.ToDate ?? todayCat;

        var invoices = await ReadScopedInvoicesAsync(
            effectiveCustomerCodes,
            sapFromDate,
            sapToDate,
            cancellationToken);

        if (invoices.Count == 0)
        {
            return new List<VanSalesLegacyOrderDto>();
        }

        var latestFiscalByDocNum = await ReadFiscalNotesAsync(
            invoices.Select(invoice => invoice.DocNum).ToList(),
            cancellationToken);

        return invoices
            .Select(invoice => VanSalesCompatibilityMapper.MapLegacyInvoice(
                invoice,
                latestFiscalByDocNum.GetValueOrDefault(invoice.DocNum)))
            .OrderByDescending(order => VanSalesCompatibilityMapper.ParseLegacyDate(order.Timestamps.CreateDate) ?? DateTime.MinValue)
            .ThenByDescending(order => order.Id)
            .ToList();
    }

    /// <summary>
    /// Reads the window's invoices for each code in scope, with the card code pushed into SAP.
    /// </summary>
    /// <remarks>
    /// One request per code rather than one request for the window filtered here afterwards. A van
    /// carries a single assigned business partner, so this is one call — and the call it replaces
    /// fetched every invoice the company raised over the same days in order to keep a handful.
    ///
    /// <para>Deduplicated on <c>DocEntry</c> because two codes in scope may name the same account
    /// under different spellings, and an invoice drawn twice would be listed twice.</para>
    /// </remarks>
    private async Task<List<Invoice>> ReadScopedInvoicesAsync(
        IReadOnlyCollection<string> effectiveCustomerCodes,
        DateTime sapFromDate,
        DateTime sapToDate,
        CancellationToken cancellationToken)
    {
        var byDocEntry = new Dictionary<int, Invoice>();

        foreach (var cardCode in effectiveCustomerCodes)
        {
            var scoped = await sapClient.GetPagedInvoicesByOffsetAsync(
                0,
                MaxInvoicesPerCustomer,
                docNum: null,
                cardCode: cardCode,
                fromDate: sapFromDate,
                toDate: sapToDate,
                vanSalesOnly: null,

                // The detail page draws the document, so the lines have to come with it. The read
                // this replaced asked for them too; without it every invoice would arrive looking
                // like one with nothing on it, which is not a shape the page can tell from the truth.
                includeDocumentLines: true,
                cancellationToken: cancellationToken);

            foreach (var invoice in scoped)
            {
                byDocEntry[invoice.DocEntry] = invoice;
            }

            if (scoped.Count == MaxInvoicesPerCustomer)
            {
                logger.LogWarning(
                    "Invoice history for {CardCode} hit the {Limit} row ceiling between {From:yyyy-MM-dd} and {To:yyyy-MM-dd}; older invoices in that window are not listed",
                    cardCode,
                    MaxInvoicesPerCustomer,
                    sapFromDate,
                    sapToDate);
            }
        }

        return byDocEntry.Values.ToList();
    }

    /// <summary>
    /// The fiscal transaction behind each invoice number, where there is one.
    /// </summary>
    /// <remarks>
    /// Keyed on the document number alone. Not on the rep: a consolidated van invoice is stamped with
    /// no user at all, and one raised at the depot is stamped with somebody else's — in both cases the
    /// verification code and QR on the row are the right ones to show, because they belong to the
    /// document rather than to whoever is holding the handset.
    ///
    /// <para>Newest first per document, so a document fiscalised more than once reports its latest
    /// attempt — which is what <see cref="VanSalesCompatibilityMapper.MapLegacyInvoice"/> reads to
    /// decide whether the invoice counts as fiscalised.</para>
    /// </remarks>
    private async Task<Dictionary<int, DesktopFiscalTransactionEntity>> ReadFiscalNotesAsync(
        IReadOnlyCollection<int> docNums,
        CancellationToken cancellationToken)
    {
        var transactions = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.DocumentType == "Invoice" &&
                docNums.Contains(transaction.DocNum))
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .ToListAsync(cancellationToken);

        return transactions
            .GroupBy(transaction => transaction.DocNum)
            .ToDictionary(group => group.Key, group => group.First());
    }
}