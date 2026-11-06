using ErrorOr;

namespace ShopInventory.Features.SapTransferApprovals;

/// <summary>
/// Who may see and add the transfers SAP is holding, and which of them: the people who may convert a
/// transfer request, on the same terms (<see cref="Services.ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync"/>).
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

/// <summary>The source warehouses a caller may add transfers from; null means every warehouse.</summary>
public sealed record SapTransferApprovalCaller(IReadOnlyList<string>? SourceWarehouses)
{
    public static SapTransferApprovalCaller Unrestricted { get; } = new((IReadOnlyList<string>?)null);

    public bool Admits(string? warehouse) =>
        SourceWarehouses is null
        || (!string.IsNullOrWhiteSpace(warehouse)
            && SourceWarehouses.Contains(warehouse.Trim(), StringComparer.OrdinalIgnoreCase));
}
