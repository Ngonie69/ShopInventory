using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;

/// <summary>
/// Posts van sales the invoice queue has fiscalised to SAP, one invoice per sale.
/// </summary>
/// <remarks>
/// <para>
/// The queue reaches a van sale by two roads: a sales order converted to an invoice, which is always
/// queued, and an online van sale that arrived while SAP was unavailable. <c>InvoicePostingJob</c>
/// fiscalises both and leaves them <c>Fiscalized</c>, and until this command existed nothing moved them
/// on. The end-of-day consolidation posts every other fiscalised entry, but refuses van sales — rightly,
/// because it would replace each sale's own <c>U_Van_saleorder</c> with a group key and the duplicate
/// check would lose sight of it. So a converted order was fiscalised, reported as accepted to the
/// handset, and never became a SAP invoice.
/// </para>
/// <para>
/// A bounded batch per run, oldest first, so a backlog clears over successive runs rather than holding
/// one run open for as long as SAP takes to accept all of it.
/// </para>
/// </remarks>
public sealed record PostQueuedVanInvoicesCommand(int BatchSize) : IRequest<ErrorOr<PostQueuedVanInvoicesResult>>;
