using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;

/// <summary>
/// Everything an operator needs to judge TransferEventListener, gathered in one read.
/// </summary>
/// <remarks>
/// One query rather than three because the numbers are only meaningful together: the counters are
/// in-memory and reset on restart, so <c>DocumentsSeen</c> means nothing without the process start
/// beside it, and neither means anything if the poll loop has stopped.
/// </remarks>
public sealed record GetTransferListenerStatusQuery(
    // How many recent documents to return. The listener holds at most twenty.
    int RecentDocumentCount = 20
) : IRequest<ErrorOr<TransferListenerStatusResult>>;
