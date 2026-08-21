using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.RecordVanSalesFiscalDayClose;

/// <summary>
/// Takes custody of the close a van handset signed for its own fiscal day, holding it until the day is
/// packaged and uploaded.
/// </summary>
public sealed record RecordVanSalesFiscalDayCloseCommand(
    VanSalesFiscalDayCloseRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesFiscalDayCloseResponse>>;
