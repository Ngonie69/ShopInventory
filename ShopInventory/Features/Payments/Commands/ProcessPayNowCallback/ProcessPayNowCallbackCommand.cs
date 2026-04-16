using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Payments.Commands.ProcessPayNowCallback;

public sealed record ProcessPayNowCallbackCommand(
    Dictionary<string, string> FormData
) : IRequest<ErrorOr<Success>>;
