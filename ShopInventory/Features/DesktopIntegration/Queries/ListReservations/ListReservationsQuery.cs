using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.ListReservations;

public sealed record ListReservationsQuery(
    string? SourceSystem,
    string? Status,
    string? CardCode,
    string? ExternalReferenceId,
    bool ActiveOnly = true,
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<ReservationListResponseDto>>;
