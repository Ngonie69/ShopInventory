using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Commands.SendFactoryTransfer;

public sealed record SendFactoryTransferCommand(
    string ClientRequestId,
    string SourceWarehouseCode,
    string? DestinationWarehouseCode,
    string Reason,
    IReadOnlyList<SendFactoryTransferLineDto> Lines,
    Guid UserId,
    string? VehiclePlate = null,
    string? DriverName = null,
    int? SourceRequestDocEntry = null) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
