using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Crates.Commands.EnsureInvoiceCrateTransaction;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.UploadInvoiceCratePod;

public sealed class UploadInvoiceCratePodHandler(
    ISAPServiceLayerClient sapClient,
    ISender mediator,
    ILogger<UploadInvoiceCratePodHandler> logger
) : IRequestHandler<UploadInvoiceCratePodCommand, ErrorOr<CratePodSubmissionDto>>
{
    public async Task<ErrorOr<CratePodSubmissionDto>> Handle(
        UploadInvoiceCratePodCommand command,
        CancellationToken cancellationToken)
    {
        var invoiceDocNum = command.InvoiceDocNum;
        if (!invoiceDocNum.HasValue || invoiceDocNum.Value <= 0)
        {
            var invoice = await sapClient.GetInvoiceByDocEntryAsync(command.InvoiceDocEntry, cancellationToken);
            if (invoice is null)
            {
                return Errors.CrateTracking.InvoiceDocEntryNotFound(command.InvoiceDocEntry);
            }

            invoiceDocNum = invoice.DocNum;
        }

        var ensureResult = await mediator.Send(
            new EnsureInvoiceCrateTransactionCommand(invoiceDocNum.Value, command.Quantity, command.UserId),
            cancellationToken);

        if (ensureResult.IsError)
        {
            return ensureResult.Errors;
        }

        if (command.FileStream.CanSeek)
        {
            command.FileStream.Position = 0;
        }

        logger.LogInformation(
            "Routing invoice-based crate POD upload for invoice doc entry {DocEntry} / doc num {DocNum} to crate transaction {CrateTransactionId}",
            command.InvoiceDocEntry,
            invoiceDocNum.Value,
            ensureResult.Value.Id);

        return await mediator.Send(
            new UploadCratePod.UploadCratePodCommand(
                ensureResult.Value.Id,
                command.SubmissionRole,
                command.Quantity,
                command.Notes,
                command.FileStream,
                command.FileName,
                command.ContentType,
                command.UserId),
            cancellationToken);
    }
}