using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.CreateSignature;

public sealed record CreateSignatureCommand(
    CreateSignatureRequest Request,
    Guid? UserId,
    string IpAddress,
    string DeviceInfo
) : IRequest<ErrorOr<DocumentSignatureDto>>;
