using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;

/// <summary>
/// One grouped SAP read for who has raised requests, then a name for each through the cached user
/// lookups. Every status rather than the tile being read, so the choices do not change under the
/// person as they move between tiles.
/// </summary>
public sealed class GetCreditNoteApprovalOriginatorsHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    ICreditNoteApprovalStageScope stageScope,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditNoteApprovalOriginatorsHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalOriginatorsQuery, ErrorOr<List<CreditNoteApprovalOriginatorDto>>>
{
    /// <summary>Names are read a few at a time, out of the application's six SAP connections.</summary>
    private const int LookupConcurrency = 3;

    public async Task<ErrorOr<List<CreditNoteApprovalOriginatorDto>>> Handle(
        GetCreditNoteApprovalOriginatorsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        var scope = await stageScope.ResolveAsync(query.CallerUserId, cancellationToken);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var ids = await sap.GetCreditNoteApprovalOriginatorIdsAsync(
            CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.All),
            scope.Value.StageCodes,
            cancellationToken);

        using var slots = new SemaphoreSlim(LookupConcurrency, LookupConcurrency);
        var originators = await Task.WhenAll(ids.Select(async id =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var user = await lookups.GetUserAsync(id, cancellationToken);
                return new CreditNoteApprovalOriginatorDto { Id = id, UserCode = user?.UserCode, Name = user?.UserName };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An unnamed user is still a choice; the page shows the key instead.
                logger.LogWarning(exception, "Could not read SAP user {UserId}; the raised-by filter shows the key instead", id);
                return new CreditNoteApprovalOriginatorDto { Id = id };
            }
            finally
            {
                slots.Release();
            }
        }));

        return originators
            .OrderBy(originator => originator.Name ?? originator.UserCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(originator => originator.Id)
            .ToList();
    }
}
