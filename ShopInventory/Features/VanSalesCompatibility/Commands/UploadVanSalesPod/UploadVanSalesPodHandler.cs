using System.Security.Cryptography;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
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

        var invoiceDocEntry = await VanSalesPodTarget.ResolveInvoiceDocEntryAsync(
            db, command.Request.Order, cancellationToken);
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

            // Every page after the first says so, and that is what makes a multi-page POD arrive.
            // UploadPodCommand drops any upload landing within its double-submit window of the same
            // uploader's last one on the same invoice, and the pages of one send arrive milliseconds
            // apart — so without this the handset's second and third pages were read as the first
            // one arriving again, and only page one was ever stored. Nothing said so: the loop
            // reported success per page and the reply below counts what was sent.
            //
            // The first page is deliberately left to the window, because that is the guard doing its
            // real job — a rep double-tapping Send. A genuine re-send of the same batch is caught
            // instead by the external reference, which is a hash of the page's own bytes: the same
            // photographs re-posted produce the same reference and are recognised page for page.
            var uploadResult = await mediator.Send(
                new UploadPodCommand(
                    invoiceDocEntry.Value,
                    stream,
                    fileName,
                    contentType,
                    user.Username,
                    user.Username,
                    externalReference,
                    user.Id,
                    IsAdditionalPage: index > 0),
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