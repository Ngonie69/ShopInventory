using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountByCode;

public sealed class GetGLAccountByCodeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetGLAccountByCodeHandler> logger
) : IRequestHandler<GetGLAccountByCodeQuery, ErrorOr<GLAccountDto>>
{
    public async Task<ErrorOr<GLAccountDto>> Handle(
        GetGLAccountByCodeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.GLAccount.SapDisabled;

        try
        {
            var account = await sapClient.GetGLAccountByCodeAsync(request.AccountCode, cancellationToken);

            if (account is null)
                return Errors.GLAccount.NotFound(request.AccountCode);

            return account;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving G/L account {AccountCode}", request.AccountCode);
            return Errors.GLAccount.SapError(ex.Message);
        }
    }
}
