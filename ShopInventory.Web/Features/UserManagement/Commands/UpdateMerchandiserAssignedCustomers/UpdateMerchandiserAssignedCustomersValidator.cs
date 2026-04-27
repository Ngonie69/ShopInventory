using FluentValidation;

namespace ShopInventory.Web.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed class UpdateMerchandiserAssignedCustomersValidator : AbstractValidator<UpdateMerchandiserAssignedCustomersCommand>
{
    public UpdateMerchandiserAssignedCustomersValidator()
    {
        RuleFor(command => command.UserId)
            .NotEmpty();

        RuleFor(command => command.Username)
            .NotEmpty();

        RuleFor(command => command.AssignedWarehouseCodes)
            .NotNull();

        RuleFor(command => command.AssignedCustomerCodes)
            .NotNull();

        RuleForEach(command => command.AssignedWarehouseCodes)
            .NotEmpty()
            .WithMessage("Assigned warehouse codes cannot be empty.");

        RuleForEach(command => command.AssignedCustomerCodes)
            .NotEmpty()
            .WithMessage("Assigned business partner codes cannot be empty.");
    }
}