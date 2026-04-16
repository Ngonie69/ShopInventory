using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetLicense;

public sealed record GetLicenseQuery() : IRequest<ErrorOr<LicenseResponse>>;
