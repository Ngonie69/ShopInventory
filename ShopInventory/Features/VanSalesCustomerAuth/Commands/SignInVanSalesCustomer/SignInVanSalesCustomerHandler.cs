using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.SignInVanSalesCustomer;

/// <summary>
/// Checks a shop's password and, if it is the right one, hands back a session.
/// </summary>
/// <remarks>
/// The counterpart to <c>VerifyVanSalesCustomerOtpHandler</c>, and it borrows that handler's
/// defences rather than inventing its own — the same account lockout, the same silence about which
/// numbers exist. What differs is what a guess costs. A code lives five minutes and dies after five
/// attempts, so guessing it is bounded by the clock; a password does not expire, so the only thing
/// standing between an attacker and an unlimited grind is the account lockout below and the
/// endpoint's rate limiter above.
/// <para>
/// Three things are therefore identical for a number that has no account, a number whose account has
/// no password, and a number whose password was typed wrong: the error, the time taken, and the
/// absence of any other trace the caller can see. The first two are worth stating because they are
/// easy to lose — an early return for "no account" would be both a faster answer and a different
/// one.
/// </para>
/// </remarks>
public sealed class SignInVanSalesCustomerHandler(
    ApplicationDbContext context,
    IVanSalesCustomerSessionIssuer sessionIssuer,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    ILogger<SignInVanSalesCustomerHandler> logger)
    : IRequestHandler<SignInVanSalesCustomerCommand, ErrorOr<VanSalesCustomerSessionResult>>
{
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;

    public async Task<ErrorOr<VanSalesCustomerSessionResult>> Handle(
        SignInVanSalesCustomerCommand command,
        CancellationToken cancellationToken)
    {
        if (!VanSalesCustomerPhone.TryNormalise(
                command.PhoneNumber,
                _settings.DefaultCountryCode,
                out var phone))
        {
            return Errors.VanSalesCustomerAuth.InvalidPhoneNumber;
        }

        var now = DateTime.UtcNow;
        var masked = VanSalesCustomerPhone.Mask(phone);

        var account = await context.VanSalesCustomerAccounts
            .FirstOrDefaultAsync(a => a.PhoneE164 == phone, cancellationToken);

        if (account is { LockedUntil: { } until } && until > now)
        {
            logger.LogWarning(
                "Refused a van sales customer sign-in for {MaskedPhone}: locked out until {LockedUntil:O}.",
                masked,
                until);
            return Errors.VanSalesCustomerAuth.TooManyAttempts;
        }

        // Always a full BCrypt verification, even with no account and no stored hash, so that the
        // registered and the unregistered number cost the same wall-clock time. See DecoyHash.
        var correct = VanSalesCustomerPassword.Verify(
            command.Password,
            account?.PasswordHash ?? VanSalesCustomerPassword.DecoyHash);

        if (!correct)
        {
            await RecordFailureAsync(account, now, cancellationToken);
            logger.LogInformation("Incorrect van sales customer password for {MaskedPhone}.", masked);
            return Errors.VanSalesCustomerAuth.InvalidCredentials;
        }

        if (account is null)
        {
            // Unreachable: the decoy hash is generated from a value nothing else holds, so a correct
            // verification implies a real account. Here so that a future change to the decoy cannot
            // turn this into a session for nobody.
            logger.LogError("A van sales customer password verified against no account for {MaskedPhone}.", masked);
            return Errors.VanSalesCustomerAuth.InvalidCredentials;
        }

        var session = await sessionIssuer.IssueAsync(
            account.Id,
            command.DeviceId,
            command.DeviceName,
            command.RequestedFromIp,
            replacesTokenHash: null,
            cancellationToken);

        if (!session.IsError)
        {
            logger.LogInformation(
                "Van sales customer {AccountId} signed in with a password from {MaskedPhone}.",
                account.Id,
                masked);
        }

        return session;
    }

    /// <summary>
    /// Count a failure against the account and lock it once they pile up.
    /// </summary>
    /// <remarks>
    /// The same counter the one-time code flow uses, on purpose. Two counters would give an attacker
    /// two budgets against one account and let them spend whichever still had attempts left.
    /// <para>
    /// Does nothing when there is no account, which is what keeps an unregistered number behaving no
    /// differently under repeated attempts than a registered one.
    /// </para>
    /// </remarks>
    private async Task RecordFailureAsync(
        Models.Entities.VanSalesCustomerAccountEntity? account,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (account is null)
        {
            return;
        }

        account.FailedOtpCount++;

        if (account.FailedOtpCount >= _settings.MaxConsecutiveFailuresBeforeLockout)
        {
            account.LockedUntil = now.AddMinutes(_settings.LockoutMinutes);
            account.FailedOtpCount = 0;

            logger.LogWarning(
                "Locked van sales customer account {AccountId} until {LockedUntil:O} after repeated failed sign-ins.",
                account.Id,
                account.LockedUntil);
        }

        account.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
    }
}
