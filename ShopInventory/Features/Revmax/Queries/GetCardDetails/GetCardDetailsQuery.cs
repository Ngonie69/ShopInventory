using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetCardDetails;

public sealed record GetCardDetailsQuery() : IRequest<ErrorOr<CardDetailsResponse>>;
