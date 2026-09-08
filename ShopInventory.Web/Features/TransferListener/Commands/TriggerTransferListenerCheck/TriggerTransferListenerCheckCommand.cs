using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.TransferListener.Commands.TriggerTransferListenerCheck;

/// <summary>
/// Asks the listener to poll SAP now rather than waiting for its next cycle.
/// </summary>
/// <remarks>
/// A write on the listener: it advances the poll window, marks documents processed and delivers
/// webhooks for anything new. Safe to repeat, since a second pass finds those documents already
/// processed.
/// </remarks>
public sealed record TriggerTransferListenerCheckCommand : IRequest<ErrorOr<TransferListenerCheckModel>>;
