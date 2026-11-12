using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Features.FactoryTransfers;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.SapApproval;

/// <summary>How a request's route stands against SAP's own transfer approval templates.</summary>
public enum SapTransferRouteKind
{
    /// <summary>The route touches a depot or a van, which keep converting straight to a transfer.</summary>
    Excluded,

    /// <summary>No active SAP template would hold this transfer.</summary>
    NotCovered,

    /// <summary>SAP would hold this transfer for approval if it were keyed in the B1 client.</summary>
    Covered
}

/// <summary>A SAP approval template that would hold a transfer.</summary>
public sealed record SapTransferTemplateMatch(int Code, string Name);

/// <summary>The verdict on one route, and why.</summary>
public sealed record SapTransferRoute(
    SapTransferRouteKind Kind,
    IReadOnlyList<SapTransferTemplateMatch> Templates,
    string? ExcludedWarehouse = null)
{
    public bool IsCovered => Kind == SapTransferRouteKind.Covered;

    /// <summary>"'Transfer To CMFS1'" or "'A' and 'B'", for a sentence.</summary>
    public string TemplateNames => Templates.Count switch
    {
        0 => string.Empty,
        1 => $"'{Templates[0].Name}'",
        _ => string.Join(", ", Templates.Take(Templates.Count - 1).Select(template => $"'{template.Name}'"))
             + $" and '{Templates[^1].Name}'"
    };
}

/// <summary>
/// Decides whether a transfer request's conversion must go to SAP's own approval: the routes SAP's
/// active transfer templates cover, except anything to or from a depot or a van.
/// </summary>
public interface ISapTransferApprovalRoutes
{
    /// <summary>
    /// The verdict for converting <paramref name="request"/>. Throws when SAP's templates cannot be
    /// read and none were read recently: a conversion must not guess.
    /// </summary>
    Task<SapTransferRoute> ForRequestAsync(InventoryTransferRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The verdict for a route, for showing on a page. Null when SAP's templates cannot be read; a list
    /// must not fail for that.
    /// </summary>
    Task<SapTransferRoute?> TryForRouteAsync(
        string? fromWarehouse,
        string? toWarehouse,
        IEnumerable<string?> lineWarehouses,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads SAP's approval templates on inventory transfers and answers, for a route, whether SAP would
/// hold a transfer on it.
/// </summary>
/// <remarks>
/// <para>SAP's templates never run for a document posted through the Service Layer (proven on the test
/// company on 2026-11-06 with "Enable Approval Process in DI" both off and on: every transfer template's
/// condition is a B1 screen query). So the app reads them itself: an active template on
/// <c>atdtInventoryTransfer</c> holds a route when it has no conditions ("always"), or when one of its
/// user queries holds for the route's source and destination (<see cref="SapTransferTemplateCondition"/>).
/// Plain terms (totals, quantities) and queries outside the one shape every transfer template uses cannot
/// be answered before the transfer exists; they are logged and do not count as holding.</para>
///
/// <para>Who adds the document in the B1 client decides, in SAP, whether a template applies at all
/// (its originator list). That person is not known here, so originators are not read: a template
/// counts for every route its condition holds for.</para>
///
/// <para>Depots and vans are left out by decision (2026-11-12): van loads and depot stock move through
/// the app's own approval only, as they do now. A depot is a centre or its shop
/// (<see cref="FactoryDepartments.Depots"/>); a van is <c>VAN</c> and a number.</para>
///
/// <para>The templates are kept for <see cref="CacheFor"/>. When a refresh fails, the last good read is
/// used for up to <see cref="LastGoodFor"/>, so a SAP blip does not stop conversions that SAP itself
/// would let through; after that a conversion refuses.</para>
/// </remarks>
public sealed class SapTransferApprovalRoutes(
    ISAPServiceLayerClient sap,
    IMemoryCache cache,
    ILogger<SapTransferApprovalRoutes> logger) : ISapTransferApprovalRoutes
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan LastGoodFor = TimeSpan.FromHours(12);

    private const string CacheKey = "sap-transfer-approval-templates";
    private const string LastGoodKey = "sap-transfer-approval-templates:last-good";
    private const string InventoryTransferDocument = "atdtInventoryTransfer";

    public async Task<SapTransferRoute> ForRequestAsync(InventoryTransferRequest request, CancellationToken cancellationToken)
    {
        var lineWarehouses = (request.StockTransferLines ?? [])
            .SelectMany(line => new[] { line.FromWarehouseCode, line.WarehouseCode });
        if (ExcludedWarehouse(request.FromWarehouse, request.ToWarehouse, lineWarehouses) is { } excluded)
            return new(SapTransferRouteKind.Excluded, [], excluded);

        var templates = await TemplatesAsync(cancellationToken);
        return Verdict(templates, request.FromWarehouse, request.ToWarehouse);
    }

    public async Task<SapTransferRoute?> TryForRouteAsync(
        string? fromWarehouse,
        string? toWarehouse,
        IEnumerable<string?> lineWarehouses,
        CancellationToken cancellationToken)
    {
        if (ExcludedWarehouse(fromWarehouse, toWarehouse, lineWarehouses) is { } excluded)
            return new(SapTransferRouteKind.Excluded, [], excluded);

        try
        {
            return Verdict(await TemplatesAsync(cancellationToken), fromWarehouse, toWarehouse);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not read SAP's transfer approval templates for {From} to {To}", fromWarehouse, toWarehouse);
            return null;
        }
    }

    /// <summary>The first depot or van among the route's warehouses, or null when it touches neither.</summary>
    public static string? ExcludedWarehouse(string? fromWarehouse, string? toWarehouse, IEnumerable<string?> lineWarehouses)
        => new[] { fromWarehouse, toWarehouse }
            .Concat(lineWarehouses)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .FirstOrDefault(code => FactoryDepartments.LocateDepotWarehouse(code) is not null ||
                                    ReceivingWarehouseOwnerRule.IsVanWarehouse(code));

    internal static SapTransferRoute Verdict(IReadOnlyList<CompiledTemplate> templates, string? fromWarehouse, string? toWarehouse)
    {
        var matches = templates
            .Where(template => template.Holds(fromWarehouse, toWarehouse))
            .Select(template => new SapTransferTemplateMatch(template.Code, template.Name))
            .ToList();

        return matches.Count == 0
            ? new(SapTransferRouteKind.NotCovered, [])
            : new(SapTransferRouteKind.Covered, matches);
    }

    private async Task<IReadOnlyList<CompiledTemplate>> TemplatesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<CompiledTemplate>? cached) && cached is not null)
            return cached;

        try
        {
            var compiled = await ReadTemplatesAsync(cancellationToken);
            cache.Set(CacheKey, compiled, CacheFor);
            cache.Set(LastGoodKey, compiled, LastGoodFor);
            return compiled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (cache.TryGetValue(LastGoodKey, out IReadOnlyList<CompiledTemplate>? lastGood) && lastGood is not null)
            {
                logger.LogWarning(exception, "Could not refresh SAP's transfer approval templates; using the last good read");
                return lastGood;
            }

            throw;
        }
    }

    private async Task<IReadOnlyList<CompiledTemplate>> ReadTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = (await sap.GetApprovalTemplateConditionsAsync(cancellationToken))
            .Where(template => IsYes(template.IsActive) &&
                               (template.ApprovalTemplateDocuments ?? []).Any(document =>
                                   string.Equals(document.DocumentType, InventoryTransferDocument, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var queryKeys = templates
            .SelectMany(template => template.ApprovalTemplateQueries ?? [])
            .Select(query => query.QueryID)
            .ToList();
        var queries = (await sap.GetUserQueriesAsync(queryKeys, cancellationToken))
            .GroupBy(query => query.InternalKey)
            .ToDictionary(group => group.Key, group => group.ToList());

        var compiled = templates.Select(template => Compile(template, queries)).ToList();

        foreach (var template in compiled.Where(template => template.Unreadable.Count > 0))
        {
            logger.LogWarning(
                "SAP approval template #{Code} '{Name}' has conditions the app cannot answer before a transfer exists, " +
                "so they never send a conversion to SAP: {Reasons}",
                template.Code, template.Name, string.Join("; ", template.Unreadable));
        }

        logger.LogInformation(
            "Read {Count} active SAP transfer approval templates ({Always} without conditions)",
            compiled.Count, compiled.Count(template => template.Always));

        return compiled;
    }

    internal static CompiledTemplate Compile(SAPApprovalTemplateCondition template, IReadOnlyDictionary<int, List<SAPUserQuery>> queries)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? $"#{template.Code}" : template.Name.Trim();
        if (!IsYes(template.UseTerms))
            return new(template.Code, name, Always: true, [], []);

        var conditions = new List<SapTransferTemplateCondition>();
        var unreadable = new List<string>();

        foreach (var reference in template.ApprovalTemplateQueries ?? [])
        {
            if (!queries.TryGetValue(reference.QueryID, out var found) || found.Count == 0)
            {
                unreadable.Add($"query {reference.QueryID} was not found");
                continue;
            }

            if (found.Count > 1)
            {
                unreadable.Add($"query {reference.QueryID} names {found.Count} saved queries");
                continue;
            }

            var condition = SapTransferTemplateCondition.Parse(found[0].Query);
            if (condition.IsReadable)
                conditions.Add(condition);
            else
                unreadable.Add($"query {reference.QueryID}: {condition.Unreadable}");
        }

        if (template.ApprovalTemplateTerms is { Count: > 0 } terms)
            unreadable.Add($"{terms.Count} plain term{(terms.Count == 1 ? string.Empty : "s")} (totals, quantities)");

        return new(template.Code, name, Always: false, conditions, unreadable);
    }

    private static bool IsYes(string? value) => string.Equals(value, "tYES", StringComparison.OrdinalIgnoreCase);

    /// <summary>An active transfer template, ready to answer for a route.</summary>
    internal sealed record CompiledTemplate(
        int Code,
        string Name,
        bool Always,
        IReadOnlyList<SapTransferTemplateCondition> Conditions,
        IReadOnlyList<string> Unreadable)
    {
        // Any one condition holding is enough: template #75 "DISPATCH" names two queries, one per
        // dispatch warehouse, and a transfer goes to one of them, never both.
        public bool Holds(string? fromWarehouse, string? toWarehouse)
            => Always || Conditions.Any(condition => condition.Matches(fromWarehouse, toWarehouse));
    }
}
