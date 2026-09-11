using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.VerifyVanSalesCustomerOtp;

/// <summary>
/// Checks a code and, if it is the right one, hands back a session.
/// </summary>
/// <remarks>
/// A six-digit code is guessable by anyone willing to try often enough, so the defences are about
/// limiting tries rather than about the code being hard:
/// <list type="number">
/// <item>A wrong attempt is counted against the code, and the code is spent once the cap is
/// reached — a fresh code costs the attacker a round trip and a cooldown.</item>
/// <item>Consecutive failures are also counted against the <em>account</em>, which locks it for a
/// while. This is the one that matters: attempts arriving from many addresses defeat the endpoint's
/// rate limiter, which partitions by caller, but they all land on the same account.</item>
/// <item>Every failure reads the same. Wrong, expired, already used and never issued are one
/// message, so a guess cannot be graded.</item>
/// </list>
/// The attempt is recorded before the code is compared, so an attacker who abandons the connection
/// mid-request still pays for the guess.
/// </remarks>
public sealed class VerifyVanSalesCustomerOtpHandler(
    ApplicationDbContext context,
    IVanSalesCustomerSessionIssuer sessionIssuer,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    IOptions<JwtSettings> jwtSettings,
    IAuditService auditService,
    ILogger<VerifyVanSalesCustomerOtpHandler> logger)
    : IRequestHandler<VerifyVanSalesCustomerOtpCommand, ErrorOr<VanSalesCustomerSessionResult>>
{
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;
    private readonly JwtSettings _jwt = jwtSettings.Value;

    /// <summary>
    /// Verifies a code, and records the attempt whichever way it went.
    /// </summary>
    /// <remarks>
    /// One row on every path, for the timing reason in <see cref="VanSalesCustomerAuditTrail"/>.
    /// Failures are recorded under the same action as a failed password so that "is somebody
    /// grinding at this account" is one filter rather than two.
    /// </remarks>
    public async Task<ErrorOr<VanSalesCustomerSessionResult>> Handle(
        VerifyVanSalesCustomerOtpCommand command,
        CancellationToken cancellationToken)
    {
        var (outcome, row) = await VerifyAsync(command, cancellationToken);
        await VanSalesCustomerAuditTrail.RecordAsync(auditService, row);
        return outcome;
    }

    private async Task<(ErrorOr<VanSalesCustomerSessionResult> Outcome, VanSalesCustomerAuditRow Row)> VerifyAsync(
        VerifyVanSalesCustomerOtpCommand command,
        CancellationToken cancellationToken)
    {
        if (!VanSalesCustomerPhone.TryNormalise(
                command.PhoneNumber,
                _settings.DefaultCountryCode,
                out var phone))
        {
            return (Errors.VanSalesCustomerAuth.InvalidPhoneNumber, Failed(
                null, null, "A sign-in code was submitted with a phone number that could not be read.",
                Errors.VanSalesCustomerAuth.InvalidPhoneNumber));
        }

        var now = DateTime.UtcNow;
        var masked = VanSalesCustomerPhone.Mask(phone);

        var account = await context.VanSalesCustomerAccounts
            .FirstOrDefaultAsync(a => a.PhoneE164 == phone, cancellationToken);

        if (account is { } known && known.LockedUntil is { } until && until > now)
        {
            logger.LogWarning(
                "Refused a van sales customer sign-in for {MaskedPhone}: locked out until {LockedUntil:O}.",
                masked,
                until);
            return (Errors.VanSalesCustomerAuth.TooManyAttempts, Failed(
                masked, known.Id, $"Code sign-in refused: the account is locked until {until:O}.",
                Errors.VanSalesCustomerAuth.TooManyAttempts));
        }

        var otp = await context.VanSalesCustomerOtps
            .Where(o => o.PhoneE164 == phone && o.ConsumedAt == null && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (otp is null)
        {
            await RecordFailureAsync(account, now, cancellationToken);
            return (Errors.VanSalesCustomerAuth.InvalidCode, Failed(
                masked, account?.Id, "Code sign-in refused: no code is outstanding for this number.",
                Errors.VanSalesCustomerAuth.InvalidCode));
        }

        if (otp.AttemptCount >= _settings.MaxOtpAttempts)
        {
            // Spend it rather than leave it sitting at the cap: an exhausted code must not remain
            // available for the next request to keep grinding against.
            otp.ConsumedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            await RecordFailureAsync(account, now, cancellationToken);

            logger.LogWarning("Van sales customer sign-in code for {MaskedPhone} exhausted its attempts.", masked);
            return (Errors.VanSalesCustomerAuth.TooManyAttempts, Failed(
                masked, account?.Id, "Code sign-in refused: the code had already been guessed at to its limit.",
                Errors.VanSalesCustomerAuth.TooManyAttempts));
        }

        // Charged before the comparison, so abandoning the request does not buy a free guess.
        otp.AttemptCount++;
        await context.SaveChangesAsync(cancellationToken);

        if (!VanSalesCustomerOtpCode.Verify(phone, command.Code!, otp.CodeHash, _jwt.SecretKey))
        {
            await RecordFailureAsync(account, now, cancellationToken);
            logger.LogInformation("Incorrect van sales customer sign-in code for {MaskedPhone}.", masked);
            return (Errors.VanSalesCustomerAuth.InvalidCode, Failed(
                masked, account?.Id,
                $"Code sign-in refused: wrong code, attempt {otp.AttemptCount} of {_settings.MaxOtpAttempts}.",
                Errors.VanSalesCustomerAuth.InvalidCode));
        }

        otp.ConsumedAt = now;
        await context.SaveChangesAsync(cancellationToken);

        if (account is null)
        {
            // Only reachable if the account was removed between the code being sent and used.
            // Reported as an invalid code, because saying "no such account" here would answer the
            // question the request endpoint spends all its effort refusing to answer.
            logger.LogWarning("A valid code for {MaskedPhone} had no account behind it.", masked);
            return (Errors.VanSalesCustomerAuth.InvalidCode, Failed(
                masked, null, "A valid code had no account behind it; it was removed after the code was sent.",
                Errors.VanSalesCustomerAuth.InvalidCode));
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
            logger.LogInformation("Van sales customer {AccountId} signed in from {MaskedPhone}.", account.Id, masked);
        }

        return (session, session.IsError
            ? Failed(masked, account.Id, "The code was right but no session could be issued.", session.FirstError)
            : new VanSalesCustomerAuditRow(
                AuditActions.VanSalesCustomerSignIn,
                masked,
                account.Id,
                $"Signed in by code from device {command.DeviceName ?? command.DeviceId ?? "(unnamed)"}.",
                Success: true));
    }

    private static VanSalesCustomerAuditRow Failed(string? masked, int? accountId, string details, Error error) =>
        new(AuditActions.VanSalesCustomerSignInFailed, masked, accountId, details, Success: false, error.Description);

    /// <summary>
    /// Count a failure against the account and lock it once they pile up.
    /// </summary>
    /// <remarks>
    /// Does nothing when there is no account, which keeps an unregistered number from behaving any
    /// differently under repeated attempts than a registered one.
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
                "Locked van sales customer account {AccountId} until {LockedUntil:O} after repeated incorrect codes.",
                account.Id,
                account.LockedUntil);
        }

        account.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
    }
}
