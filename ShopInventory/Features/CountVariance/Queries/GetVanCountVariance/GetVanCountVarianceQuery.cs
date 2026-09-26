using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CountVariance.Queries.GetVanCountVariance;

/// <summary>
/// Every van's latest SAP inventory count dated within the range, valued at the van sales price list
/// excluding VAT, and added up across the fleet.
/// </summary>
/// <param name="FromDate">First count date, inclusive.</param>
/// <param name="ToDate">Last count date, inclusive.</param>
public sealed record GetVanCountVarianceQuery(DateTime FromDate, DateTime ToDate)
    : IRequest<ErrorOr<VanCountVarianceReportDto>>;
