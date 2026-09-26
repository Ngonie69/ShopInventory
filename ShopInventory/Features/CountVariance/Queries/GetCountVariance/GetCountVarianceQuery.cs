using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CountVariance.Queries.GetCountVariance;

/// <summary>
/// One SAP inventory count's variance, valued excluding VAT at the van sales price list.
/// </summary>
public sealed record GetCountVarianceQuery(int DocumentEntry) : IRequest<ErrorOr<CountVarianceReportDto>>;
