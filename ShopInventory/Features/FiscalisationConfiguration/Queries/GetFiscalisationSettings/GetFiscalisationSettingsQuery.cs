using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationSettings;

public sealed record GetFiscalisationSettingsQuery : IRequest<ErrorOr<FiscalisationSettingsDto>>;
