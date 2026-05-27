using System.Security.Cryptography;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPod;

public sealed class UploadVanSalesPodHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ISAPServiceLayerClient sapClient
) : IRequestHandler<UploadVanSalesPodCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        UploadVanSalesPodCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        if (command.Request.Order <= 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidPodTarget",
                "A valid order or invoice reference is required for POD upload.");
        }

        if (command.Request.Images.Count == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingPodImages",
                "Please capture the invoice first.");
        }

        var invoiceDocEntry = await ResolveInvoiceDocEntryAsync(command.Request.Order, cancellationToken);
        if (!invoiceDocEntry.HasValue)
        {
            return Error.NotFound(
                "VanSalesCompatibility.InvoiceNotFound",
                "The selected document is not linked to a posted invoice yet.");
        }

        var invoice = await sapClient.GetInvoiceByDocEntryAsync(invoiceDocEntry.Value, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(
                "VanSalesCompatibility.InvoiceNotFound",
                "The target invoice could not be found in SAP.");
        }

        for (var index = 0; index < command.Request.Images.Count; index++)
        {
            var (bytes, contentType, fileExtension) = DecodeImage(command.Request.Images[index].Image);
            if (bytes.Length == 0)
            {
                return Error.Validation(
                    "VanSalesCompatibility.InvalidPodImage",
                    $"POD image {index + 1} is empty or invalid.");
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var fileName = $"mobile-pod-{invoiceDocEntry.Value}-{index + 1}.{fileExtension}";
            var externalReference = BuildExternalReference(invoiceDocEntry.Value, bytes);

            var uploadResult = await mediator.Send(
                new UploadPodCommand(
                    invoiceDocEntry.Value,
                    stream,
                    fileName,
                    contentType,
                    user.Username,
                    user.Username,
                    externalReference,
                    user.Id),
                cancellationToken);

            if (uploadResult.IsError)
            {
                return uploadResult.Errors;
            }
        }

        return command.Request.Images.Count == 1
            ? "POD uploaded successfully"
            : $"{command.Request.Images.Count} POD files uploaded successfully";
    }

    private async Task<int?> ResolveInvoiceDocEntryAsync(int legacyOrderId, CancellationToken cancellationToken)
    {
        var salesOrder = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Id == legacyOrderId)
            .Select(order => new
            {
                InvoiceSapDocEntry = order.Invoice != null ? order.Invoice.SAPDocEntry : null
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (salesOrder is not null)
        {
            return salesOrder.InvoiceSapDocEntry;
        }

        var invoiceDocEntry = await db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.SAPDocEntry == legacyOrderId || invoice.Id == legacyOrderId)
            .Select(invoice => invoice.SAPDocEntry)
            .FirstOrDefaultAsync(cancellationToken);

        return invoiceDocEntry > 0 ? invoiceDocEntry : legacyOrderId;
    }

    private static (byte[] Bytes, string ContentType, string FileExtension) DecodeImage(string encodedImage)
    {
        var trimmed = encodedImage?.Trim() ?? string.Empty;
        var contentType = "image/jpeg";
        var payload = trimmed;

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = trimmed.IndexOf(',');
            if (separatorIndex <= 5)
            {
                return (Array.Empty<byte>(), contentType, "jpg");
            }

            var metadata = trimmed[5..separatorIndex];
            var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (metadataParts.Length > 0 && metadataParts[0].Contains('/'))
            {
                contentType = metadataParts[0];
            }

            payload = trimmed[(separatorIndex + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            var extension = contentType.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/webp" => "webp",
                "application/pdf" => "pdf",
                _ => "jpg"
            };

            return (bytes, contentType, extension);
        }
        catch
        {
            return (Array.Empty<byte>(), contentType, "jpg");
        }
    }

    private static string BuildExternalReference(int docEntry, byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var hashSegment = Convert.ToHexString(hash)[..16];
        return $"MOBILE-POD-{docEntry}-{hashSegment}";
    }
}