using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountsByType;

public sealed class GetGLAccountsByTypeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetGLAccountsByTypeHandler> logger
) : IRequestHandler<GetGLAccountsByTypeQuery, ErrorOr<GLAccountListResponseDto>>
{
    public async Task<ErrorOr<GLAccountListResponseDto>> Handle(
        GetGLAccountsByTypeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.GLAccount.SapDisabled;

        try
        {
            var accounts = await sapClient.GetGLAccountsByTypeAsync(request.AccountType, cancellationToken);

            return new GLAccountListResponseDto
            {
                TotalCount = accounts.Count,
                Accounts = accounts
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving G/L accounts by type {AccountType}", request.AccountType);
            return Errors.GLAccount.SapError(ex.Message);
        }
    }
}
