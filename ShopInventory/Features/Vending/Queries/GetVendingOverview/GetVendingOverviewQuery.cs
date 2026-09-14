using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Vending.Queries.GetVendingOverview;

/// <summary>
/// Every vending account and every vendor under the business partners those accounts sell on.
/// </summary>
public sealed record GetVendingOverviewQuery : IRequest<ErrorOr<VendingOverviewDto>>;
