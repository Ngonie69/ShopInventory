using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.UserManagement.Commands.UpdateDriverBusinessPartnerAccess;

public sealed record UpdateDriverBusinessPartnerAccessCommand(
    List<string> AssignedCustomerCodes,
    string? ModifiedBy
) : IRequest<ErrorOr<int>>;