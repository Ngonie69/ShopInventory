using FluentValidation;

namespace ShopInventory.Features.Backups.Commands.CreateBackup;

public sealed class CreateBackupValidator : AbstractValidator<CreateBackupCommand>
{
    private static readonly string[] AllowedBackupTypes = ["Full", "Incremental", "Differential"];

    public CreateBackupValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.Request)
            .NotNull().WithMessage("Request body is required.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.BackupType)
                .NotEmpty().WithMessage("Backup type is required.")
                .Must(backupType => AllowedBackupTypes.Contains(backupType, StringComparer.OrdinalIgnoreCase))
                .WithMessage("Backup type must be Full, Incremental, or Differential.");

            RuleFor(x => x.Request.Description)
                .MaximumLength(500).WithMessage("Description must not exceed 500 characters.")
                .When(x => !string.IsNullOrWhiteSpace(x.Request.Description));
        });
    }
}