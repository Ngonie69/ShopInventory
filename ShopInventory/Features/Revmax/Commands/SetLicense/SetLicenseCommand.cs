using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Commands.SetLicense;

public sealed record SetLicenseCommand(string License) : IRequest<ErrorOr<LicenseResponse>>;
