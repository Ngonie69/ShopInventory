using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetDriverBusinessPartnerAccess;

public sealed record GetDriverBusinessPartnerAccessQuery()
    : IRequest<ErrorOr<GetDriverBusinessPartnerAccessResult>>;