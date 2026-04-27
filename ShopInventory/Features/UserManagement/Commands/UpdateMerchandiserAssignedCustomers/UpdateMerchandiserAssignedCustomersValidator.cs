using FluentValidation;

namespace ShopInventory.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed class UpdateMerchandiserAssignedCustomersValidator : AbstractValidator<UpdateMerchandiserAssignedCustomersCommand>
{
    public UpdateMerchandiserAssignedCustomersValidator()
    {
        RuleFor(command => command.Id)
            .NotEmpty();

        RuleFor(command => command.Request.AssignedWarehouseCodes)
            .NotNull();

        RuleFor(command => command.Request.AssignedCustomerCodes)
            .NotNull();

        RuleForEach(command => command.Request.AssignedWarehouseCodes)
            .NotEmpty()
            .WithMessage("Assigned warehouse codes cannot be empty.");

        RuleForEach(command => command.Request.AssignedCustomerCodes)
            .NotEmpty()
            .WithMessage("Assigned customer codes cannot be empty.");
    }
}