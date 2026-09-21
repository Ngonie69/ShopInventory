using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

public sealed class GetDesktopSalesHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<DesktopSalePostingSettings> tillPostingSettings,
    IOptions<VanSalesPostingSettings> vanPostingSettings)
    : IRequestHandler<GetDesktopSalesQuery, ErrorOr<DesktopSalesListResult>>
{
    /// <summary>
    /// Lists sales, and records whose takings were read.
    /// </summary>
    /// <remarks>
    /// One of the two desktop reads the blanket filter deliberately leaves to its handler. This one
    /// shows a shop's money, and the scope resolver below exists because the warehouse used to arrive
    /// unchecked from the query string — so a refused read is exactly the event worth keeping: it is
    /// either a client bug or somebody reaching for another shop's takings, and nothing else records
    /// that it happened.
    /// </remarks>
    public async Task<ErrorOr<DesktopSalesListResult>> Handle(
        GetDesktopSalesQuery request, CancellationToken cancellationToken)
    {
        var outcome = await ReadAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            string.IsNullOrWhiteSpace(request.WarehouseCode) ? "(caller scope)" : request.WarehouseCode.Trim(),
            outcome.IsError
                ? $"Refused a sales read for warehouse {request.WarehouseCode}."
                : $"Read {outcome.Value.Sales.Count} of {outcome.Value.TotalCount} sale(s), "
                    + $"page {outcome.Value.Page}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<DesktopSalesListResult>> ReadAsync(
        GetDesktopSalesQuery request, CancellationToken cancellationToken)
    {
        // Resolved here rather than in the controller so that no caller can reach these rows without
        // being scoped. The warehouse used to arrive from the query string unchecked.
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == request.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        // Refused rather than narrowed when a confined caller names another shop, and narrowed rather
        // than widened when it names none — see DesktopSalesReadScope.NarrowMany, the many-warehouse form
        // of the rule the analysis shares.
        //
        // Both parameters go in. A caller may name one warehouse or a set of them; they are the same
        // filter, so the singular is folded into the plural here and nothing downstream knows which form
        // the request arrived in.
        var requestedWarehouses = Combine(request.WarehouseCode, request.Warehouses);
        var narrowed = scope.Value.NarrowMany(requestedWarehouses);
        if (narrowed.IsError)
        {
            return narrowed.Errors;
        }

        // Two separate things, and they must stay separate. `scopeWarehouse` is whose money the caller
        // may read at all and is applied to every query below, the facet counts included. `warehouses`
        // is the console's own warehouse filter, which a facet count for the warehouse group has to lift
        // in order to be any use. Folding them into one would either leak another shop's counts or count
        // only the chip already pressed.
        var scopeWarehouse = scope.Value.WarehouseCode;
        var warehouses = narrowed.Value;

        var sources = Combine(request.SourceSystem, request.SourceSystems);
        var consolidationStatuses = ParseStatuses<DesktopSaleConsolidationStatus>(
            Combine(request.ConsolidationStatus, request.ConsolidationStatuses));
        var fiscalStatuses = ParseStatuses<DesktopSaleFiscalizationStatus>(
            Combine(null, request.FiscalizationStatuses));
        var paymentMethods = Combine(null, request.PaymentMethods);

        // Everything that is not one of the five chip groups: the period, the caller's scope, the search,
        // the customer, the amount window and the tender comparison. It is the floor every count on this
        // page stands on — a facet lifts its own group and nothing else.
        IQueryable<DesktopSaleEntity> Common()
        {
            var q = db.DesktopSales.AsNoTracking().AsQueryable();

            if (scopeWarehouse is not null)
                q = q.Where(s => s.WarehouseCode == scopeWarehouse);

            if (request.FromDate.HasValue)
                q = q.Where(s => s.DocDate >= request.FromDate.Value.Date);

            if (request.ToDate.HasValue)
                q = q.Where(s => s.DocDate <= request.ToDate.Value.Date);

            return q;
        }

        // The source scope, decided rather than inherited. This is a list of sales, and an online van
        // sale's row is not one — it carries the receipt a handset signed for a sale that lives in SAP
        // and in its confirmed StockReservation. Every money column on this DTO therefore describes a
        // sale the caller is already looking at somewhere else, so the default answer excludes it and a
        // caller that wants those rows asks for them by name.
        //
        // Findable rather than hidden: the row is real, an operator chasing a fiscal reference has to be
        // able to reach it, and the fiscalisation console is not a document list. The filter is plain
        // equality so `?sourceSystem=KefalosVanSalesOnline` returns exactly them.
        static IQueryable<DesktopSaleEntity> WithSources(
            IQueryable<DesktopSaleEntity> q, IReadOnlyList<string> picked)
        {
            if (picked.Count == 0)
            {
                return q.Where(s => s.SourceSystem != SaleSourceSystems.VanSalesOnline);
            }

            var (named, blank) = SplitBlank(picked);

            return blank
                // Trimmed, because the facet folds whitespace in with the blanks — see Text(). A
                // predicate that only matched "" would count rows the chip then could not reach.
                ? q.Where(s => s.SourceSystem == null || s.SourceSystem.Trim() == "" || named.Contains(s.SourceSystem))
                : q.Where(s => s.SourceSystem != null && named.Contains(s.SourceSystem));
        }

        // One filtered query, with one chip group lifted. `FilterGroup.None` is the real list — the page,
        // the total and the "filtered from" denominator's numerator — and each of the five others is the
        // query behind that group's counts.
        IQueryable<DesktopSaleEntity> Filtered(FilterGroup lift)
        {
            var q = Common();

            q = WithSources(q, lift == FilterGroup.Source ? [] : sources);

            if (lift != FilterGroup.Warehouse && warehouses.Count > 0)
                q = q.Where(s => warehouses.Contains(s.WarehouseCode));

            if (lift != FilterGroup.Consolidation && consolidationStatuses.Count > 0)
                q = q.Where(s => consolidationStatuses.Contains(s.ConsolidationStatus));

            if (lift != FilterGroup.Fiscalization && fiscalStatuses.Count > 0)
                q = q.Where(s => fiscalStatuses.Contains(s.FiscalizationStatus));

            // The one group whose blank is a real answer somebody looks for: a sale the till rang up
            // without recording what was handed over. It cannot ride in the IN list, so it is split
            // out — and when it is the only thing picked, the empty list makes the IN false and the
            // predicate reduces to the blanks alone.
            if (lift != FilterGroup.PaymentMethod && paymentMethods.Count > 0)
            {
                var (named, blank) = SplitBlank(paymentMethods);

                q = blank
                    ? q.Where(s => s.PaymentMethod == null || s.PaymentMethod.Trim() == "" || named.Contains(s.PaymentMethod))
                    : q.Where(s => s.PaymentMethod != null && named.Contains(s.PaymentMethod));
            }

            if (!string.IsNullOrEmpty(request.CardCode))
                q = q.Where(s => s.CardCode == request.CardCode);

            if (request.MinTotal.HasValue)
                q = q.Where(s => s.TotalAmount >= request.MinTotal.Value);

            if (request.MaxTotal.HasValue)
                q = q.Where(s => s.TotalAmount <= request.MaxTotal.Value);

            // What the drawer took against what the invoice says. A till rounds cash up, so "over" is the
            // change the customer was handed; "under" is money the day is short.
            q = request.PaymentDifference?.Trim().ToLowerInvariant() switch
            {
                DesktopSalesPaymentDifferences.Exact => q.Where(s => s.AmountPaid == s.TotalAmount),
                DesktopSalesPaymentDifferences.Under => q.Where(s => s.AmountPaid < s.TotalAmount),
                DesktopSalesPaymentDifferences.Over => q.Where(s => s.AmountPaid > s.TotalAmount),
                _ => q
            };

            // Upper on both sides rather than ILike, so the same filter runs on SQLite under test.
            //
            // A number is tried three ways, because a person searching is holding a piece of paper and
            // the number on it could be any of them: the sale number the receipt prints, the SAP document
            // the sale posted as, or a fragment of the device reference. "INV10427" is only ever the
            // first, so a search that names the prefix does not also drag in a SAP invoice that happens
            // to share the digits — which, on a table where SAP numbers run past 770000, it eventually
            // would.
            //
            // The customer is swept for as text alongside the references. The console's search box offers
            // it, and a name is the thing an operator has when they have no paper at all.
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var term = request.Search.Trim().ToUpperInvariant();
                int? saleId = DesktopSaleNumber.TryParse(term, out var parsedId) ? parsedId : null;

                // "INV10427" can only be a sale number, so it is not also tried as a SAP document or
                // swept for as text. A bare "10427" is tried as both numbers, because a person holding a
                // printed document could have either in front of them.
                var exact = DesktopSaleNumber.NamesSaleNumber(term);
                int? docNum = !exact && int.TryParse(term, out var parsed) ? parsed : null;
                q = q.Where(s =>
                    (saleId != null && s.Id == saleId) ||
                    (docNum != null && s.SapDocNum == docNum) ||
                    (!exact && (
                        s.ExternalReferenceId.ToUpper().Contains(term) ||
                        (s.FiscalReceiptNumber != null && s.FiscalReceiptNumber.ToUpper().Contains(term)) ||
                        s.CardCode.ToUpper().Contains(term) ||
                        (s.CardName != null && s.CardName.ToUpper().Contains(term)) ||
                        (s.RouteCustomerCode != null && s.RouteCustomerCode.ToUpper().Contains(term)) ||
                        (s.RouteCustomerName != null && s.RouteCustomerName.ToUpper().Contains(term)))));
            }

            return q;
        }

        var query = Filtered(FilterGroup.None);

        var totalCount = await query.CountAsync(cancellationToken);

        // The period and the caller's scope and nothing else. Asked for only when a console says it will
        // draw it: it is a second COUNT over the same window, and a till polling this endpoint has no use
        // for a denominator it never shows.
        var unfilteredCount = request.IncludeFacets
            ? await WithSources(Common(), []).CountAsync(cancellationToken)
            : totalCount;

        var facets = request.IncludeFacets
            ? await ReadFacetsAsync(Filtered, cancellationToken)
            : DesktopSalesFacets.Empty;

        // Projected in two steps rather than one. The posting-eligibility rule is a method — it has
        // to be, because the console and the posting command must refuse identically — and a method
        // cannot be translated to SQL, so the enums it reads are carried out of the database
        // alongside the row and the rule is applied to the page in memory.
        var rows = await OrderBy(query.Include(s => s.Lines), request.Sort)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(s => new
            {
                s.ConsolidationStatus,
                s.FiscalizationStatus,
                s.FiscalizationRequiresReconciliation,
                s.PostIssuedAtUtc,
                Sale = new DesktopSaleListItemDto(
                s.Id,
                // Filled below, with the rest of what a method has to compute. The formatter cannot be
                // translated to SQL, and the format must not be a second copy written out in a query.
                string.Empty,
                s.ExternalReferenceId,
                s.SourceSystem,
                s.CardCode,
                s.CardName,
                s.DocDate,
                s.TotalAmount,
                s.VatAmount,
                s.Currency,
                s.FiscalizationStatus.ToString(),
                s.FiscalReceiptNumber,
                s.FiscalQRCode,
                s.FiscalVerificationCode,
                s.FiscalDeviceNumber,
                s.FiscalDayNo,
                s.FiscalError,
                s.FiscalizationAttempts,
                s.ConsolidationStatus.ToString(),
                s.ConsolidationId,
                s.WarehouseCode,
                s.PaymentMethod,
                s.PaymentReference,
                s.AmountPaid,
                s.CreatedBy,
                // Filled in below, once the accounts on this page have been looked up.
                null,
                s.CreatedAt,
                s.RouteCustomerId,
                s.RouteCustomerCode,
                s.RouteCustomerName,
                s.SapDocEntry,
                s.SapDocNum,
                s.PostedAt,
                s.PostingAttempts,
                s.LastPostingError,
                s.PaymentStatus,
                s.PaymentSapDocNum,
                // Filled in below, where the rule can actually be called.
                null,
                null,
                s.Lines.Select(l => new DesktopSaleLineItemDto(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.Quantity,
                    l.UnitPrice,
                    l.LineTotal,
                    l.WarehouseCode,
                    l.TaxCode,
                    l.DiscountPercent
                )).ToList())
            })
            .ToListAsync(cancellationToken);

        // One lookup for the page rather than a join per row: a page of a shop's takings is rung up by
        // a handful of accounts, and CreatedBy is a plain string column with no navigation to join on.
        var operators = await SaleOperatorNames.ResolveAsync(
            db, rows.Select(row => row.Sale.CreatedBy), cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var usesPlatform = fiscalisationSettings.Value.UsesPlatform;

        var sales = rows
            .Select(row => row.Sale with
            {
                SaleNumber = DesktopSaleNumber.Format(row.Sale.Id),
                CreatedByName = SaleOperatorNames.Label(row.Sale.CreatedBy, operators),
                PostRefusal = DesktopSalePostEligibility.Refusal(
                    row.Sale.SourceSystem, row.ConsolidationStatus, row.FiscalizationStatus, row.Sale.SapDocNum),
                FiscaliseRefusal = DesktopSaleFiscalisationRetry.ManualRefusal(
                    row.Sale.SourceSystem,
                    row.FiscalizationStatus,
                    row.FiscalizationRequiresReconciliation,
                    row.Sale.CreatedAt,
                    nowUtc,
                    usesPlatform),
                PostHeldUntilUtc = PostHeldUntil(row.Sale, row.PostIssuedAtUtc, nowUtc)
            })
            .ToList();

        return new DesktopSalesListResult(
            sales,
            totalCount,
            request.Page,
            request.PageSize,
            (request.Page * request.PageSize) < totalCount,
            unfilteredCount,
            facets
        );
    }

    /// <summary>
    /// When a sale held after a post whose outcome is unknown may be sent again, or null when it is
    /// not held. The window is the one the route that posts the sale waits out.
    /// </summary>
    private DateTime? PostHeldUntil(DesktopSaleListItemDto sale, DateTime? postIssuedAtUtc, DateTime nowUtc)
    {
        if (sale.SapDocEntry.HasValue || postIssuedAtUtc is not { } issuedAt)
        {
            return null;
        }

        var graceMinutes = string.Equals(sale.SourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal)
            ? vanPostingSettings.Value.UnresolvedPostGraceMinutes
            : tillPostingSettings.Value.UnresolvedPostGraceMinutes;

        return UnresolvedPostHold.IsHeld(issuedAt, graceMinutes, nowUtc)
            ? UnresolvedPostHold.RetryAfterUtc(issuedAt, graceMinutes)
            : null;
    }

    /// <summary>The filter group one facet count lifts, or <see cref="None"/> for the list itself.</summary>
    private enum FilterGroup
    {
        None,
        Consolidation,
        Fiscalization,
        Warehouse,
        PaymentMethod,
        Source
    }

    /// <summary>
    /// The singular parameter and its plural twin, as one list.
    /// </summary>
    /// <remarks>
    /// Blank entries are dropped and the rest trimmed, because a console sending an empty chip value
    /// would otherwise filter to sales whose warehouse is the empty string — a filter that matches
    /// nothing and reads as a page that has broken.
    /// </remarks>
    private static IReadOnlyList<string> Combine(string? single, IReadOnlyList<string>? many) =>
        (many ?? [])
            .Append(single)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The named statuses that are real members of the enum.
    /// </summary>
    /// <remarks>
    /// Silently dropping a name nobody defined matches what the singular filter did before there were
    /// plurals: an unparseable <c>consolidationStatus</c> was ignored rather than refused. Dropping is
    /// also the only safe reading of a partly-unknown set — a console one release ahead of this API would
    /// otherwise have its whole filter refused because of one chip.
    /// </remarks>
    /// <summary>
    /// A picked set, split into the values that are stored and whether the blank was among them.
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopSalesFilterValues.Blank"/> stands for a column that is empty, which no
    /// <c>IN</c> list can express — so it comes out here and the caller ORs it back on.
    /// </remarks>
    private static (List<string> Named, bool Blank) SplitBlank(IReadOnlyList<string> picked)
    {
        var named = picked
            .Where(value => !string.Equals(value, DesktopSalesFilterValues.Blank, StringComparison.Ordinal))
            .ToList();

        return (named, named.Count != picked.Count);
    }

    private static List<TStatus> ParseStatuses<TStatus>(IReadOnlyList<string> named) where TStatus : struct, Enum =>
        named
            .Select(name => Enum.TryParse<TStatus>(name, true, out var parsed) ? parsed : (TStatus?)null)
            .Where(parsed => parsed.HasValue)
            .Select(parsed => parsed!.Value)
            .Distinct()
            .ToList();

    /// <summary>
    /// The page order, with a tiebreak so that paging cannot show one sale twice.
    /// </summary>
    /// <remarks>
    /// Every order ends on the primary key. Two sales rung up in the same second — or, on the money
    /// orders, for the same amount — have no order of their own, and a database is free to return them
    /// in a different one on each page's query, which drops a row off the boundary between two pages
    /// and repeats another.
    /// </remarks>
    private static IOrderedQueryable<DesktopSaleEntity> OrderBy(IQueryable<DesktopSaleEntity> query, string? sort) =>
        sort?.Trim().ToLowerInvariant() switch
        {
            DesktopSalesSortOrders.Oldest =>
                query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id),
            DesktopSalesSortOrders.TotalDescending =>
                query.OrderByDescending(s => s.TotalAmount).ThenByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id),
            DesktopSalesSortOrders.TotalAscending =>
                query.OrderBy(s => s.TotalAmount).ThenByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id),
            // By the name the console shows, falling back to the code it shows when there is no name.
            // Ordering on the code alone would look unsorted on a page whose Customer column reads names.
            DesktopSalesSortOrders.Customer =>
                query.OrderBy(s => s.CardName ?? s.CardCode).ThenByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id),
            _ => query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
        };

    /// <summary>
    /// What each chip group would return, counted with that group's own selection lifted.
    /// </summary>
    /// <remarks>
    /// Five grouped counts over the same window the list is drawn from. They are grouped rather than
    /// counted per value so that a group costs one query whatever its number of chips — the payment
    /// methods and warehouses are whatever the tills have actually written, not a list this code knows.
    /// </remarks>
    private static async Task<DesktopSalesFacets> ReadFacetsAsync(
        Func<FilterGroup, IQueryable<DesktopSaleEntity>> filtered,
        CancellationToken cancellationToken)
    {
        var consolidation = await filtered(FilterGroup.Consolidation)
            .GroupBy(s => s.ConsolidationStatus)
            .Select(g => new StatusFacetRow<DesktopSaleConsolidationStatus>(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var fiscalization = await filtered(FilterGroup.Fiscalization)
            .GroupBy(s => s.FiscalizationStatus)
            .Select(g => new StatusFacetRow<DesktopSaleFiscalizationStatus>(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var warehouse = await filtered(FilterGroup.Warehouse)
            .GroupBy(s => s.WarehouseCode)
            .Select(g => new TextFacetRow(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var payment = await filtered(FilterGroup.PaymentMethod)
            .GroupBy(s => s.PaymentMethod)
            .Select(g => new TextFacetRow(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var source = await filtered(FilterGroup.Source)
            .GroupBy(s => s.SourceSystem)
            .Select(g => new TextFacetRow(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        return new DesktopSalesFacets(
            consolidation.Select(row => new DesktopSalesFacet(row.Value.ToString()!, row.Count)).ToList(),
            fiscalization.Select(row => new DesktopSalesFacet(row.Value.ToString()!, row.Count)).ToList(),
            Text(warehouse),
            Text(payment),
            Text(source));
    }

    private sealed record StatusFacetRow<TStatus>(TStatus Value, int Count) where TStatus : struct, Enum;

    private sealed record TextFacetRow(string? Value, int Count);

    /// <summary>
    /// Text facets, with null and blank folded together and the largest group first.
    /// </summary>
    /// <remarks>
    /// A sale whose tender was never recorded is still a sale, and a chip it can be reached by is the only
    /// way anybody finds the ones the till left blank — so the blanks are carried rather than dropped. Null
    /// and "" arrive as separate groups from the database and are one thing to a reader, so they are added
    /// together, under <see cref="DesktopSalesFilterValues.Blank"/> rather than under the empty string:
    /// this value goes straight back out as a filter, and the empty string is the one value that cannot
    /// survive that round trip — a query string drops it and an <c>IN</c> list cannot express it.
    /// </remarks>
    private static List<DesktopSalesFacet> Text(IEnumerable<TextFacetRow> rows) =>
        rows
            .GroupBy(
                row => string.IsNullOrWhiteSpace(row.Value) ? DesktopSalesFilterValues.Blank : row.Value.Trim(),
                StringComparer.Ordinal)
            .Select(g => new DesktopSalesFacet(g.Key, g.Sum(row => row.Count)))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Value, StringComparer.Ordinal)
            .ToList();
}
