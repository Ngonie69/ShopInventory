using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccounts;

public sealed class GetGLAccountsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetGLAccountsHandler> logger
) : IRequestHandler<GetGLAccountsQuery, ErrorOr<GLAccountListResponseDto>>
{
    public async Task<ErrorOr<GLAccountListResponseDto>> Handle(
        GetGLAccountsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.GLAccount.SapDisabled;

        try
        {
            var accounts = await sapClient.GetGLAccountsAsync(cancellationToken);

            return new GLAccountListResponseDto
            {
                TotalCount = accounts.Count,
                Accounts = accounts
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving G/L accounts");
            return Errors.GLAccount.SapError(ex.Message);
        }
    }
}
