using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// The SAP approval stages a caller may read and decide. <see cref="StageCodes"/> is null for a caller
/// who sees every stage.
/// </summary>
public sealed record CreditNoteApprovalStageFilter(IReadOnlyList<string> StageNames, IReadOnlySet<int>? StageCodes)
{
    public static readonly CreditNoteApprovalStageFilter EveryStage = new([], null);

    public bool Admits(int? stageCode) => StageCodes is null || stageCode is int code && StageCodes.Contains(code);

    /// <summary>'Wash Bay Approvals', or 'A' or 'B' — for the messages that name the scope.</summary>
    public string Describe() => string.Join(" or ", StageNames.Select(name => $"'{name}'"));
}

/// <summary>
/// Narrows SAP's credit memo approval queue for a role that must not see all of it — the wash bay, which
/// decides only what waits at its own stage.
/// </summary>
/// <remarks>
/// The app decides every request as one SAP service approver, so SAP's own stage approvers say nothing
/// about which person in the app may click. Without this, anyone holding <c>creditnotes.approve</c> could
/// decide any stage that service approver sits on.
/// </remarks>
public interface ICreditNoteApprovalStageScope
{
    /// <summary>
    /// The stages the account <paramref name="userId"/> may see. Null is a service caller with no account
    /// — an integration key on its own — which sees every stage.
    /// </summary>
    Task<ErrorOr<CreditNoteApprovalStageFilter>> ResolveAsync(Guid? userId, CancellationToken cancellationToken);
}

/// <remarks>
/// Keyed on the account's role in the database, never on the role claims of the request. The Web calls
/// the API with its integration key and the signed-in user's token together, so the principal carries
/// the key's Admin role beside the user's own; the first live run read the role claim, got Admin, and
/// showed the wash bay all 9,729 requests.
/// </remarks>
public sealed class CreditNoteApprovalStageScope(
    ApplicationDbContext context,
    IOptions<CreditNoteApprovalSettings> settings,
    ISapApprovalLookups lookups,
    ILogger<CreditNoteApprovalStageScope> logger) : ICreditNoteApprovalStageScope
{
    public async Task<ErrorOr<CreditNoteApprovalStageFilter>> ResolveAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return CreditNoteApprovalStageFilter.EveryStage;
        }

        var role = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId.Value && user.IsActive)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);

        // An account that is gone or disabled is refused rather than treated as unscoped: the integration
        // key riding on the same request would otherwise carry it past every permission check.
        if (role is null)
        {
            return Errors.Auth.UserNotFound;
        }

        return await ResolveForRoleAsync(role, cancellationToken);
    }

    public async Task<ErrorOr<CreditNoteApprovalStageFilter>> ResolveForRoleAsync(string? role, CancellationToken cancellationToken)
    {
        var names = ConfiguredStageNames(settings.Value, role);
        if (names.Count == 0)
        {
            // Fail closed for a role that must be scoped: a missing setting must not hand it the whole queue.
            return ApplicationRoles.RequiresCreditNoteStageScope(role)
                ? Errors.CreditNoteApproval.StageScopeNotConfigured(role!.Trim())
                : CreditNoteApprovalStageFilter.EveryStage;
        }

        IReadOnlyList<SAPApprovalStage> stages;
        try
        {
            stages = await lookups.GetStagesByNameAsync(names, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the SAP approval stages the {Role} role is scoped to", role);
            return Errors.CreditNoteApproval.SapUnavailable(exception.Message);
        }

        // SAP compares names as its column collates; matching again here keeps out a stage whose name
        // only resembles the configured one.
        var codes = stages
            .Where(stage => stage.Name is not null && names.Contains(stage.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(stage => stage.Code)
            .ToHashSet();

        var filter = new CreditNoteApprovalStageFilter(names, codes);
        if (codes.Count == 0)
        {
            return Errors.CreditNoteApproval.StageScopeUnresolved(filter.Describe());
        }

        return filter;
    }

    internal static IReadOnlyList<string> ConfiguredStageNames(CreditNoteApprovalSettings settings, string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return [];
        }

        var configured = settings.RoleStageScopes
            .Where(pair => string.Equals(pair.Key.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value ?? []);

        return configured
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
