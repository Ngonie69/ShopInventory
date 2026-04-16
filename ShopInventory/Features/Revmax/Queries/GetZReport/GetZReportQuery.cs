using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetZReport;

public sealed record GetZReportQuery() : IRequest<ErrorOr<ZReportResponse>>;
