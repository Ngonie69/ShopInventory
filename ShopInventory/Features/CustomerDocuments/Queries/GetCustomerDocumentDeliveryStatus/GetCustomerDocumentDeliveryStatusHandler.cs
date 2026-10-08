using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryStatus;

public sealed class GetCustomerDocumentDeliveryStatusHandler(
    ApplicationDbContext context,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    IOptions<CustomerDocumentDeliverySettings> options,
    ILogger<GetCustomerDocumentDeliveryStatusHandler> logger)
    : IRequestHandler<GetCustomerDocumentDeliveryStatusQuery, ErrorOr<CustomerDocumentDeliveryStatusDto>>
{
    public async Task<ErrorOr<CustomerDocumentDeliveryStatusDto>> Handle(
        GetCustomerDocumentDeliveryStatusQuery query,
        CancellationToken cancellationToken) =>
        await CustomerDocumentStatusReader.ReadAsync(
            context, openWaClient, openWaOptions.Value, options.Value, logger, cancellationToken);
}
