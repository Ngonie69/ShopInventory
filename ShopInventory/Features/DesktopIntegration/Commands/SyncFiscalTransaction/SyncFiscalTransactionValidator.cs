using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

public sealed class SyncFiscalTransactionValidator : AbstractValidator<SyncFiscalTransactionCommand>
{
    public SyncFiscalTransactionValidator()
    {
        RuleFor(command => command.Request.ClientTransactionId)
            .MaximumLength(100)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.ClientTransactionId));

        RuleFor(command => command.Request.DocNum)
            .GreaterThan(0);

        RuleFor(command => command.Request.DocumentType)
            .NotEmpty()
            .MaximumLength(40);

        RuleFor(command => command.Request.Status)
            .NotEmpty()
            .MaximumLength(40);

        RuleFor(command => command.Request.Message)
            .MaximumLength(2000)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.Message));

        RuleFor(command => command.Request.VerificationCode)
            .MaximumLength(120)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.VerificationCode));

        RuleFor(command => command.Request.QRCode)
            .MaximumLength(2000)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.QRCode));

        RuleFor(command => command.Request.DeviceSerialNumber)
            .MaximumLength(120)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.DeviceSerialNumber));

        RuleFor(command => command.Request.DeviceId)
            .MaximumLength(120)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.DeviceId));

        RuleFor(command => command.Request.FiscalDay)
            .MaximumLength(40)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.FiscalDay));

        RuleFor(command => command.Request.CardCode)
            .MaximumLength(50)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.CardCode));

        RuleFor(command => command.Request.CardName)
            .MaximumLength(255)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.CardName));

        RuleFor(command => command.Request.Currency)
            .MaximumLength(10)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.Currency));

        RuleFor(command => command.Request.OriginalInvoiceNumber)
            .MaximumLength(50)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.OriginalInvoiceNumber));

        RuleFor(command => command.Request.SourceSystem)
            .MaximumLength(50)
            .When(command => !string.IsNullOrWhiteSpace(command.Request.SourceSystem));
    }
}