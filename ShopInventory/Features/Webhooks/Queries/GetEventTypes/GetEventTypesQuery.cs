using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Queries.GetEventTypes;

public sealed record GetEventTypesQuery() : IRequest<ErrorOr<WebhookEventTypesResponse>>;
