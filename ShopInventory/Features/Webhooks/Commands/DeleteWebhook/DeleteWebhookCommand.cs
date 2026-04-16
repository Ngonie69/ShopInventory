using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Webhooks.Commands.DeleteWebhook;

public sealed record DeleteWebhookCommand(int Id) : IRequest<ErrorOr<Deleted>>;
