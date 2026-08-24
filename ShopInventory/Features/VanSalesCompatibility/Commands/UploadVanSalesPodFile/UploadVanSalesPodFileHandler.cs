using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPodFile;

public sealed class UploadVanSalesPodFileHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ISAPServiceLayerClient sapClient,
    ILogger<UploadVanSalesPodFileHandler> logger
) : IRequestHandler<UploadVanSalesPodFileCommand, ErrorOr<DocumentAttachmentDto>>
{
    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadVanSalesPodFileCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        if (command.Order <= 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidPodTarget",
                "A valid order or invoice reference is required for POD upload.");
        }

        var docEntry = await VanSalesPodTarget.ResolveInvoiceDocEntryAsync(db, command.Order, cancellationToken);
        if (!docEntry.HasValue)
        {
            return Error.NotFound(
                "VanSalesCompatibility.InvoiceNotFound",
                "The selected document is not linked to a posted invoice yet.");
        }

        var invoice = await sapClient.GetInvoiceByDocEntryAsync(docEntry.Value, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(
                "VanSalesCompatibility.InvoiceNotFound",
                "The target invoice could not be found in SAP.");
        }

        logger.LogInformation(
            "Van sales POD page for order {Order} resolved to invoice {DocEntry} (additional page: {IsAdditionalPage})",
            command.Order, docEntry.Value, command.IsAdditionalPage);

        // The caller's own reference is passed through untouched. The handset mints one per photo that
        // is identical on every retry — invoice, uploader, when the draft was saved, and the photo's
        // own hash — so a page re-sent by a background worker after the app was killed is recognised
        // as the same page rather than stored twice.
        return await mediator.Send(
            new UploadPodCommand(
                docEntry.Value,
                command.FileStream,
                command.FileName,
                command.ContentType,
                command.Description,
                user.Username,
                command.ExternalReference,
                user.Id,
                command.IsAdditionalPage),
            cancellationToken);
    }
}
