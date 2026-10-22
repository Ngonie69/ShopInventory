using FluentValidation;

namespace ShopInventory.Features.RouteCustomers.Commands.ImportRouteSchedule;

/// <summary>
/// The shape of an upload only. Whether each outlet is a shop on the truck is the handler's answer,
/// given outlet by outlet rather than by refusing the request.
/// </summary>
public sealed class ImportRouteScheduleValidator : AbstractValidator<ImportRouteScheduleCommand>
{
    public const int MaxSheets = 20;

    /// <summary>The October 2026 workbook holds about 1,650 outlets across five trucks.</summary>
    public const int MaxOutlets = 5000;

    public ImportRouteScheduleValidator()
    {
        RuleFor(command => command.Request).NotNull();

        When(command => command.Request is not null, () =>
        {
            RuleFor(command => command.Request.Sheets)
                .NotEmpty().WithMessage("The workbook has no sheets with a calling schedule on them.")
                .Must(sheets => sheets is null || sheets.Count <= MaxSheets)
                .WithMessage($"Upload at most {MaxSheets} sheets at a time.")
                .Must(sheets => sheets is null || sheets.Sum(sheet => sheet?.Outlets?.Count ?? 0) <= MaxOutlets)
                .WithMessage($"Upload at most {MaxOutlets} outlets at a time.");

            RuleForEach(command => command.Request.Sheets).ChildRules(sheet =>
            {
                sheet.RuleFor(s => s.Name).NotEmpty().MaximumLength(100);
                sheet.RuleFor(s => s.AccountCode).MaximumLength(100);
                sheet.RuleFor(s => s.Outlets).NotNull();
                sheet.RuleForEach(s => s.Outlets).ChildRules(outlet =>
                {
                    outlet.RuleFor(o => o.Ref).NotEmpty().MaximumLength(150);
                    outlet.RuleFor(o => o.Name).NotEmpty().MaximumLength(300);

                    // A value outside the week would be saved as a day no date can ever fall on.
                    outlet.RuleFor(o => o.Day).IsInEnum().WithMessage("That is not a day of the week.");
                });
            });
        });
    }
}
