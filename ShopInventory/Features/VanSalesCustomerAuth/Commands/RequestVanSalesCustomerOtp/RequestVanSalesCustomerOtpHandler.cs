using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;

/// <summary>
/// Issues a sign-in code, or quietly does nothing, and reports the same result either way.
/// </summary>
/// <remarks>
/// Every early return below is <em>success</em>, not an error, and each one is deliberate. The
/// endpoint is unauthenticated and takes a phone number, so any observable difference between a
/// number we know and one we do not turns it into a tool for reading off a supplier's customer
/// list. The only failure it can report is a number that is not a number at all — which reveals
/// nothing, because the caller already knew what they typed.
/// <para>
/// The cases that return silently: no account, an account switched off, an account locked out after
/// repeated wrong codes, and a number that asked again too soon. All four are indistinguishable
/// from a code being sent, and the last one is what stops this endpoint being used to make a
/// customer's phone ring all night.
/// </para>
/// </remarks>
public sealed class RequestVanSalesCustomerOtpHandler(
    ApplicationDbContext context,
    IVanSalesCustomerOtpSender sender,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    IOptions<JwtSettings> jwtSettings,
    ILogger<RequestVanSalesCustomerOtpHandler> logger)
    : IRequestHandler<RequestVanSalesCustomerOtpCommand, ErrorOr<RequestVanSalesCustomerOtpResult>>
{
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;
    private readonly JwtSettings _jwt = jwtSettings.Value;

    public async Task<ErrorOr<RequestVanSalesCustomerOtpResult>> Handle(
        RequestVanSalesCustomerOtpCommand command,
        CancellationToken cancellationToken)
    {
        // The uniform answer, built once and returned from every path below.
        var uniformResult = new RequestVanSalesCustomerOtpResult(
            _settings.ResendCooldownSeconds,
            _settings.OtpTtlMinutes * 60);

        if (!VanSalesCustomerPhone.TryNormalise(
                command.PhoneNumber,
                _settings.DefaultCountryCode,
                out var phone))
        {
            return Errors.VanSalesCustomerAuth.InvalidPhoneNumber;
        }

        var now = DateTime.UtcNow;

        var account = await context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Where(a => a.PhoneE164 == phone)
            .Select(a => new
            {
                a.Id,
                a.IsActive,
                a.LockedUntil
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null || !account.IsActive)
        {
            logger.LogInformation(
                "A van sales customer sign-in code was requested for {MaskedPhone}, which has no active account. Answering as though it were sent.",
                VanSalesCustomerPhone.Mask(phone));
            return uniformResult;
        }

        if (account.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            logger.LogWarning(
                "Suppressed a van sales customer sign-in code for {MaskedPhone}: the account is locked until {LockedUntil:O}.",
                VanSalesCustomerPhone.Mask(phone),
                lockedUntil);
            return uniformResult;
        }

        var cooldownStart = now.AddSeconds(-_settings.ResendCooldownSeconds);
        var sentRecently = await context.VanSalesCustomerOtps
            .AsNoTracking()
            .AnyAsync(o => o.PhoneE164 == phone && o.CreatedAt > cooldownStart, cancellationToken);

        if (sentRecently)
        {
            logger.LogInformation(
                "Suppressed a van sales customer sign-in code for {MaskedPhone}: one was sent within the cooldown.",
                VanSalesCustomerPhone.Mask(phone));
            return uniformResult;
        }

        // Any code still outstanding for this number is retired before a new one is issued, so a
        // customer who asks twice cannot end up with two live codes and no idea which to type.
        await context.VanSalesCustomerOtps
            .Where(o => o.PhoneE164 == phone && o.ConsumedAt == null && o.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ConsumedAt, now), cancellationToken);

        var code = VanSalesCustomerOtpCode.Generate(_settings.OtpLength);

        var otp = new VanSalesCustomerOtpEntity
        {
            PhoneE164 = phone,
            CodeHash = VanSalesCustomerOtpCode.Hash(phone, code, _jwt.SecretKey),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_settings.OtpTtlMinutes),
            RequestedFromIp = command.RequestedFromIp
        };

        context.VanSalesCustomerOtps.Add(otp);
        await context.SaveChangesAsync(cancellationToken);

        // Delivery is attempted after the code is committed: a message that arrives for a code we
        // failed to save is worse than a code with no message, because the customer would type a
        // valid-looking code that could never verify.
        var channel = await sender.SendAsync(phone, code, cancellationToken);

        otp.DeliveryChannel = channel.ToString();
        await context.SaveChangesAsync(cancellationToken);

        return uniformResult;
    }
}
