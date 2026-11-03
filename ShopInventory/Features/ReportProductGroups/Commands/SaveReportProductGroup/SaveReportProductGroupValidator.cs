using FluentValidation;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ReportProductGroups.Commands.SaveReportProductGroup;

public sealed class SaveReportProductGroupValidator : AbstractValidator<SaveReportProductGroupCommand>
{
    public SaveReportProductGroupValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Members).NotNull();
        RuleFor(x => x.Members.Count).LessThanOrEqualTo(2000).When(x => x.Members is not null);
        RuleForEach(x => x.Members).ChildRules(member =>
        {
            member.RuleFor(m => m.Kind).IsInEnum();
            member.RuleFor(m => m.Code).NotEmpty().MaximumLength(50);
            member.RuleFor(m => m.Name).MaximumLength(200);
            member.RuleFor(m => m.Code)
                .Must(code => int.TryParse(code, out var number) && number > 0)
                .When(m => m.Kind == ReportProductGroupMemberKind.ItemGroup)
                .WithMessage("An SAP item group is named by its number.");
        });
    }
}
