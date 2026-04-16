using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetReceivablesAging;

public sealed record GetReceivablesAgingQuery() : IRequest<ErrorOr<ReceivablesAgingReportDto>>;
