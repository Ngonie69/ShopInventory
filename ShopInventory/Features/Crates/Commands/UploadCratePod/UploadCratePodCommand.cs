using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Commands.UploadCratePod;

public sealed record UploadCratePodCommand(
    int CrateTransactionId,
    string? SubmissionRole,
    decimal Quantity,
    string? Notes,
    Stream FileStream,
    string FileName,
    string ContentType,
    Guid? UserId
) : IRequest<ErrorOr<CratePodSubmissionDto>>;