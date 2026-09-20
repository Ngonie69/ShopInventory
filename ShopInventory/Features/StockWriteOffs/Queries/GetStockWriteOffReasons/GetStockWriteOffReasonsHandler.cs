using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;

/// <summary>
/// Offers SAP's own reason list where the company database defines one, and the configured list
/// otherwise.
/// </summary>
/// <remarks>
/// Never both, and never a merge. SAP rejects any value its reason field does not define, so a picker
/// showing a locally-invented reason alongside SAP's would offer a choice that fails at post time.
/// Where SAP defines nothing, <see cref="StockWriteOffReasonsResponseDto.RecordedInSap"/> says so, and
/// the reason is kept on the local record and in the document's comments instead.
/// </remarks>
public sealed class GetStockWriteOffReasonsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<StockWriteOffSettings> settings,
    ILogger<GetStockWriteOffReasonsHandler> logger)
    : IRequestHandler<GetStockWriteOffReasonsQuery, ErrorOr<StockWriteOffReasonsResponseDto>>
{
    public async Task<ErrorOr<StockWriteOffReasonsResponseDto>> Handle(
        GetStockWriteOffReasonsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var sapReasons = await sapClient.GetGoodsIssueLineReasonsAsync(cancellationToken);
            if (sapReasons.Count > 0)
            {
                return new StockWriteOffReasonsResponseDto
                {
                    RecordedInSap = true,
                    Reasons = sapReasons
                        .Select(reason => new StockWriteOffReasonDto
                        {
                            Value = reason.Value,
                            Description = reason.Description
                        })
                        .ToList()
                };
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the goods issue reasons from SAP; offering the configured list");
        }

        return new StockWriteOffReasonsResponseDto
        {
            RecordedInSap = false,
            Reasons = settings.Value.Reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Select(reason => new StockWriteOffReasonDto
                {
                    Value = reason.Trim(),
                    Description = reason.Trim()
                })
                .ToList()
        };
    }
}
