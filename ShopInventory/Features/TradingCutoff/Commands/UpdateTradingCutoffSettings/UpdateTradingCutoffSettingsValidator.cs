using FluentValidation;

namespace ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

public sealed class UpdateTradingCutoffSettingsValidator : AbstractValidator<UpdateTradingCutoffSettingsCommand>
{
    public UpdateTradingCutoffSettingsValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.Channels)
            .NotNull()
            .Must(channels => channels.Select(c => c.Channel?.Trim().ToLowerInvariant()).Distinct().Count() == channels.Count)
            .WithMessage("Each channel may be listed once.");

        RuleForEach(x => x.Channels).ChildRules(channel =>
        {
            channel.RuleFor(c => c.Channel)
                .Must(value => TradingCutoffKeys.Channels.Contains(value?.Trim().ToLowerInvariant()))
                .WithMessage(c => $"'{c.Channel}' is not a channel. Use shops, vending or vans.");

            channel.RuleFor(c => c.CutoffTime)
                .Must(BeATradingTime)
                .WithMessage(c => $"'{c.CutoffTime}' is not a cut-off time. Use HH:mm, after 00:00.");
        });

        RuleFor(x => x.Accounts)
            .NotNull()
            .Must(accounts => accounts.Select(a => a.UserId).Distinct().Count() == accounts.Count)
            .WithMessage("Each account may have one exception.");

        RuleForEach(x => x.Accounts).ChildRules(account =>
        {
            account.RuleFor(a => a.UserId).NotEmpty();

            account.RuleFor(a => a.Mode)
                .Must(mode => Enum.TryParse<TradingCutoffAccountMode>(mode, ignoreCase: true, out _))
                .WithMessage(a => $"'{a.Mode}' is not an exception. Use FollowChannel, Exempt or CutOff.");

            account.RuleFor(a => a.CutoffTime)
                .Must(BeATradingTime)
                .When(a => !string.IsNullOrWhiteSpace(a.CutoffTime))
                .WithMessage(a => $"'{a.CutoffTime}' is not a cut-off time. Use HH:mm, after 00:00.");
        });
    }

    /// <summary>
    /// 00:00 is refused: it would close the whole day, which is the maintenance lockout's job, not this.
    /// </summary>
    private static bool BeATradingTime(string? value) =>
        TradingCutoffKeys.TryParseTime(value, out var time) && time > TimeOnly.MinValue;
}
