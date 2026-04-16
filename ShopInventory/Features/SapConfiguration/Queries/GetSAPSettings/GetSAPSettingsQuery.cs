using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapConfiguration.Queries.GetSAPSettings;

public sealed record GetSAPSettingsQuery() : IRequest<ErrorOr<SAPConnectionSettingsDto>>;
