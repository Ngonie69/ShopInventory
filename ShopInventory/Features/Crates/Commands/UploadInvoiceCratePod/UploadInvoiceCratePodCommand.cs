using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Commands.UploadInvoiceCratePod;

public sealed record UploadInvoiceCratePodCommand(
    int InvoiceDocEntry,
    int? InvoiceDocNum,
    string? SubmissionRole,
    decimal Quantity,
    string? Notes,
    Stream FileStream,
    string FileName,
    string ContentType,
    Guid? UserId
) : IRequest<ErrorOr<CratePodSubmissionDto>>;