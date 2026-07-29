using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPod;

public sealed record UploadVanSalesPodCommand(
    VanSalesPodUploadRequest Request,
    Guid UserId) : IRequest<ErrorOr<string>>;