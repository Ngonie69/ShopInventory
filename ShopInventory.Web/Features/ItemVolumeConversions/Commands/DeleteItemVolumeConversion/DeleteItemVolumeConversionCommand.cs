using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Commands.DeleteItemVolumeConversion;

public sealed record DeleteItemVolumeConversionCommand(string ItemCode) : IRequest<ErrorOr<Deleted>>;
