using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.UserManagement.Commands.RefreshDriverBusinessPartnerAccess;

public sealed record RefreshDriverBusinessPartnerAccessCommand()
    : IRequest<ErrorOr<int>>;