using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetVanCountVariance;

/// <summary>Every van's latest count dated in the range, valued and added up across the fleet.</summary>
public sealed record GetVanCountVarianceQuery(DateTime FromDate, DateTime ToDate)
    : IRequest<ErrorOr<VanCountVarianceReport>>;
