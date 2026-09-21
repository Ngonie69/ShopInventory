using FluentValidation;

namespace ShopInventory.Features.SapUsers.Commands.ChangeSapUserPassword;

/// <summary>
/// Shape only. <b>SAP's own password policy is the authority</b> — its security level decides
/// minimum length, character classes, reuse and expiry, it varies by company, and it is enforced on
/// the PATCH whatever passes here. These rules exist to catch the obvious before a round trip and to
/// keep something SAP would truncate or refuse out of the request; they are deliberately not an
/// attempt to mirror the policy, because a mirror that drifts refuses passwords SAP would accept.
/// </summary>
public sealed class ChangeSapUserPasswordValidator : AbstractValidator<ChangeSapUserPasswordCommand>
{
    /// <summary>Under SAP's lowest security level a password may be this short.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>What SAP's <c>OUSR.U_PASSWORD</c> column holds.</summary>
    public const int MaxPasswordLength = 32;

    public ChangeSapUserPasswordValidator()
    {
        RuleFor(command => command.InternalKey).GreaterThan(0);

        RuleFor(command => command.NewPassword)
            .NotEmpty().WithMessage("A new password is required.")
            .MinimumLength(MinPasswordLength)
                .WithMessage($"A SAP password must be at least {MinPasswordLength} characters.")
            .MaximumLength(MaxPasswordLength)
                .WithMessage($"A SAP password cannot be longer than {MaxPasswordLength} characters.")
            // A leading or trailing space survives the round trip and then cannot be typed back
            // reliably at the B1 sign-in, which reads as the new password simply not working.
            .Must(password => password is not null && password.Trim().Length == password.Length)
                .WithMessage("A SAP password cannot start or end with a space.");
    }
}
