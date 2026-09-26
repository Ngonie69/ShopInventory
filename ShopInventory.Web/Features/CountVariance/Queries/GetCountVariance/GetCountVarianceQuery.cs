using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetCountVariance;

/// <summary>One SAP inventory count's variance, valued at the van sales price list excluding VAT.</summary>
public sealed record GetCountVarianceQuery(int DocumentEntry) : IRequest<ErrorOr<CountVarianceReport>>;
