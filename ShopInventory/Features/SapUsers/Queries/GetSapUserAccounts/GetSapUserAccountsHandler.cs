using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapUsers.Queries.GetSapUserAccounts;

/// <summary>
/// Reads the company's SAP user accounts straight from the Service Layer. Nothing is cached: the
/// question this screen answers — who is locked out right now — is the one thing a cache would get
/// wrong, and it would get it wrong at exactly the moment somebody is standing at the desk waiting.
/// </summary>
public sealed class GetSapUserAccountsHandler(
    ISAPServiceLayerClient sap,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetSapUserAccountsHandler> logger)
    : IRequestHandler<GetSapUserAccountsQuery, ErrorOr<SapUserAccountListResponseDto>>
{
    public async Task<ErrorOr<SapUserAccountListResponseDto>> Handle(
        GetSapUserAccountsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.SapUser.SapDisabled;
        }

        List<SAPUserAccount> accounts;
        try
        {
            accounts = await sap.GetSapUserAccountsAsync(query.Search, query.LockedOnly, cancellationToken);
        }
        catch (SapRequestRejectedException ex)
        {
            logger.LogWarning(ex, "SAP refused the user account list");
            return Errors.SapUser.Rejected(ex.SapMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to read SAP user accounts");
            return Errors.SapUser.Unreachable;
        }

        var items = accounts.Select(SapUserAccountMapping.ToDto).ToList();

        return new SapUserAccountListResponseDto
        {
            Items = items,
            TotalCount = items.Count,
            LockedCount = items.Count(item => item.IsLocked),
            // The counts describe what came back, so say when that was not everything: under a
            // truncated read "3 locked" means three on this page, not three in the company.
            Truncated = accounts.Count >= SapUserAccountLimits.PageSize
        };
    }
}
