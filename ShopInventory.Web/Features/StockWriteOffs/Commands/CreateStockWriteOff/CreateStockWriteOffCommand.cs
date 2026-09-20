using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.StockWriteOffs.Commands.CreateStockWriteOff;

public sealed record CreateStockWriteOffCommand(CreateStockWriteOffRequest Request)
    : IRequest<ErrorOr<StockWriteOffResult>>;
