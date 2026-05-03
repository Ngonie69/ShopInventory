using FluentValidation;

namespace ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;

public sealed class UpdateGlobalDriverAssignedCustomersValidator : AbstractValidator<UpdateGlobalDriverAssignedCustomersCommand>
{
    public UpdateGlobalDriverAssignedCustomersValidator()
    {
        RuleFor(command => command.Request.AssignedCustomerCodes)
            .NotNull();

        RuleForEach(command => command.Request.AssignedCustomerCodes)
            .NotEmpty()
            .WithMessage("Assigned customer codes cannot be empty.");
    }
}