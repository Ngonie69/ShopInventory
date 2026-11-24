using ErrorOr;
using ShopInventory.Models;

namespace ShopInventory.Features.SapTransferApprovals;

/// <summary>
/// Who may see, decide and add the transfers SAP is holding, and which of them.
/// </summary>
/// <remarks>
/// Resolved from the account in the database, by user id — never from the role claims on the request.
/// The Web calls the API with its integration key and the user's token together, so those claims
/// include the key's Admin and would admit everybody.
/// </remarks>
public interface ISapTransferApprovalAccess
{
    /// <param name="userId">The caller's account; null for a service caller, which sees every warehouse.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ErrorOr<SapTransferApprovalCaller>> ResolveAsync(Guid? userId, CancellationToken cancellationToken);
}

/// <summary>
/// The warehouses a caller may approve and add held transfers for; <see cref="Warehouses"/> null means
/// every warehouse. With <see cref="EitherEnd"/> a draft is theirs when it leaves <em>or enters</em> one
/// of them — the wash bay and a shop's stock user, who approve and add what comes in as well as what goes
/// out. Without it, only drafts leaving one of them — a depot controller, as for a conversion.
/// </summary>
public sealed record SapTransferApprovalCaller(IReadOnlyList<string>? Warehouses, bool EitherEnd = false)
{
    public static SapTransferApprovalCaller Unrestricted { get; } = new((IReadOnlyList<string>?)null);

    /// <summary>
    /// Whether the caller sees every warehouse's held drafts. An either-end caller does, though it acts
    /// only on its own (user, 2026-11-24: "shopstock and washbay should be able to see all warehouse under
    /// sap held transfers"). A depot controller sees only what it may add.
    /// </summary>
    public bool ReadsEveryDraft => Warehouses is null || EitherEnd;

    /// <summary>The source warehouses SAP filters the queue on; null when the caller reads every draft.</summary>
    public IReadOnlyList<string>? SourceWarehouses => ReadsEveryDraft ? null : Warehouses;

    /// <summary>Whether the caller may read <paramref name="draft"/> at all — its row and its drawer.</summary>
    public bool MayRead(SAPStockTransferDraft draft) => ReadsEveryDraft || AdmitsDraft(draft);

    /// <summary>Whether <paramref name="warehouse"/> is one of the caller's (always, for an unscoped caller).</summary>
    public bool Admits(string? warehouse) =>
        Warehouses is null
        || (!string.IsNullOrWhiteSpace(warehouse)
            && Warehouses.Contains(warehouse.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a draft between these warehouses is the caller's: with <see cref="EitherEnd"/>, one of
    /// theirs anywhere on it; otherwise every source one of theirs, and at least one source named.
    /// </summary>
    public bool AdmitsRoute(IEnumerable<string?> sources, IEnumerable<string?> destinations)
    {
        if (Warehouses is null)
        {
            return true;
        }

        var from = sources.Where(code => !string.IsNullOrWhiteSpace(code)).ToList();
        if (EitherEnd)
        {
            return from.Concat(destinations).Any(Admits);
        }

        return from.Count > 0 && from.All(Admits);
    }

    /// <summary><see cref="AdmitsRoute"/> on the draft's header and line warehouses.</summary>
    public bool AdmitsDraft(SAPStockTransferDraft draft) =>
        AdmitsRoute(
            SapTransferApprovalProjection.SourceWarehouses(draft),
            SapTransferApprovalProjection.DestinationWarehouses(draft));

    /// <summary>
    /// The warehouse to name when <see cref="AdmitsDraft"/> refuses: the first source not the caller's.
    /// </summary>
    public string OutsideWarehouse(SAPStockTransferDraft draft) =>
        SapTransferApprovalProjection.SourceWarehouses(draft).FirstOrDefault(code => !Admits(code))
        ?? draft.FromWarehouse
        ?? "an unnamed warehouse";
}
