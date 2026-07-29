using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed record FiscalizeCrossDeviceCreditNoteCommand(
    string CreditNoteNumber,
    string OriginalInvoiceNumber,
    int? RefDeviceId,
    long? RefReceiptGlobalNo,
    int? RefFiscalDayNo,
    string? Currency,
    string? BranchName,
    string? CustomerName,
    string? CustomerVatNumber,
    string? CustomerAddress,
    string? CustomerTelephone,
    string? CustomerEmail,
    string? CustomerBPN,
    string? InvoiceComment) : IRequest<ErrorOr<FiscalizeCrossDeviceCreditNoteResult>>;