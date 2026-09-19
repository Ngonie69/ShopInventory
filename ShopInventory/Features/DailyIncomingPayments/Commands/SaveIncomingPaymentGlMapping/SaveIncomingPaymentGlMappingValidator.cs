using FluentValidation;
using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

public sealed class SaveIncomingPaymentGlMappingValidator : AbstractValidator<SaveIncomingPaymentGlMappingCommand>
{
    /// <summary>More than this is a mailing list, which belongs in the mail server.</summary>
    public const int MaxRecipients = 20;

    public SaveIncomingPaymentGlMappingValidator()
    {
        RuleFor(x => x.CardCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.CardName).MaximumLength(200);
        RuleFor(x => x.CashAccount).NotEmpty().MaximumLength(20);
        RuleFor(x => x.ElectronicAccount).NotEmpty().MaximumLength(20);

        RuleFor(x => x.Run)
            .Must(run => Enum.TryParse<DailyPaymentRun>(run, ignoreCase: true, out _))
            .WithMessage("Run must be Shops or Vans.");

        RuleFor(x => x.NotifyEmails)
            .Must(value => !IncomingPaymentGlMappingAddresses.Invalid(value).Any())
            .WithMessage(x => $"Not an email address: {string.Join(", ", IncomingPaymentGlMappingAddresses.Invalid(x.NotifyEmails))}.")
            .Must(value => IncomingPaymentGlMappingAddresses.Parse(value).Count <= MaxRecipients)
            .WithMessage($"At most {MaxRecipients} people can be emailed.")
            .Must(value => (IncomingPaymentGlMappingAddresses.Normalise(value)?.Length ?? 0) <= 1000)
            .WithMessage("The recipient list is too long.");
    }
}
