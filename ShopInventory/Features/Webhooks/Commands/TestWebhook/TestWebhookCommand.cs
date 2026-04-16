using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Commands.TestWebhook;

public sealed record TestWebhookCommand(
    int Id,
    TestWebhookRequest Request
) : IRequest<ErrorOr<TestWebhookResponse>>;
