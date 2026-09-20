using FluentValidation;

namespace ShopInventory.Web.Features.StockWriteOffs.Commands.CreateStockWriteOff;

public sealed class CreateStockWriteOffValidator : AbstractValidator<CreateStockWriteOffCommand>
{
    public CreateStockWriteOffValidator()
    {
        RuleFor(command => command.Request.WarehouseCode)
            .NotEmpty().WithMessage("Choose the warehouse the stock is leaving.");

        RuleFor(command => command.Request.Reason)
            .NotEmpty().WithMessage("A write-off needs a reason.");

        RuleFor(command => command.Request.Lines)
            .NotEmpty().WithMessage("Add at least one line before writing anything off.");
    }
}
