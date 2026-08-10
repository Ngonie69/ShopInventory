using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;

/// <summary>
/// Takes custody of sales a van handset already completed and stamped, holding them until the end-of-day
/// posting run. Nothing here touches SAP.
/// </summary>
public sealed record IngestVanSalesOfflineSalesCommand(
    VanSalesOfflineSaleBatchRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesOfflineSaleBatchResponse>>;
