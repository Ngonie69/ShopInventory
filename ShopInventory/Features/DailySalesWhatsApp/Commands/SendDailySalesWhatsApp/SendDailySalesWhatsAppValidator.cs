using FluentValidation;
using ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

public sealed class SendDailySalesWhatsAppValidator : AbstractValidator<SendDailySalesWhatsAppCommand>
{
    public SendDailySalesWhatsAppValidator()
    {
        RuleFor(x => x.Recipients)
            .Must(list => list!.Count(number => !string.IsNullOrWhiteSpace(number)) <= UpdateDailySalesWhatsAppSettingsValidator.MaxRecipients)
            .When(x => x.Recipients is not null)
            .WithMessage($"At most {UpdateDailySalesWhatsAppSettingsValidator.MaxRecipients} numbers can receive the report.");

        RuleFor(x => x.TradingDate)
            .Must(date => date!.Value.Year >= 2020)
            .When(x => x.TradingDate is not null)
            .WithMessage("Choose a trading day from 2020 on.");
    }
}
