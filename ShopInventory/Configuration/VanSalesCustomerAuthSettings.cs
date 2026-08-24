namespace ShopInventory.Configuration;

/// <summary>
/// Tuning for the van sales customer sign-in: how long a code lives, how many times it may be got
/// wrong, and how long a handset stays signed in.
/// </summary>
/// <remarks>
/// Separate from <see cref="JwtSettings"/> because these govern a different subject with different
/// risk. A customer's access token is short-lived and their refresh token long-lived — the opposite
/// balance to staff, and deliberately so: a shopkeeper on a bad line cannot be asked to re-request
/// an OTP every hour, while a token that leaks must stop being useful quickly.
/// </remarks>
public class VanSalesCustomerAuthSettings
{
    /// <summary>
    /// Country code applied to a local number typed without one, including the leading <c>+</c>.
    /// </summary>
    /// <remarks>
    /// Customers type their number the way they say it — <c>0771234567</c> — and the account is
    /// keyed on E.164. Configurable rather than hardcoded so the same build serves another country.
    /// </remarks>
    public string DefaultCountryCode { get; set; } = "+263";

    /// <summary>Digits in the one-time code. Six is what people expect to read off a message.</summary>
    public int OtpLength { get; set; } = 6;

    /// <summary>
    /// How long a code stays usable. Short, because the code is the entire credential — but long
    /// enough to survive a slow WhatsApp delivery on a poor connection.
    /// </summary>
    public int OtpTtlMinutes { get; set; } = 5;

    /// <summary>Verification attempts allowed against a single code before it is spent.</summary>
    public int MaxOtpAttempts { get; set; } = 5;

    /// <summary>
    /// Consecutive failures across codes before the account itself is locked. Guards the case the
    /// endpoint limiter cannot see: many attempts from many addresses against one number.
    /// </summary>
    public int MaxConsecutiveFailuresBeforeLockout { get; set; } = 10;

    /// <summary>How long that lockout lasts.</summary>
    public int LockoutMinutes { get; set; } = 30;

    /// <summary>
    /// Shortest gap between two code requests for the same number, so the request endpoint cannot
    /// be used to bombard a customer's phone.
    /// </summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>Access token lifetime. Short; the refresh token carries the session.</summary>
    public int AccessTokenExpirationMinutes { get; set; } = 30;

    /// <summary>
    /// Refresh token lifetime. Long on purpose — the app is used a few times a week and must not
    /// demand a fresh OTP every visit.
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 90;

    /// <summary>
    /// The OpenWA session that carries OTP messages.
    /// </summary>
    /// <remarks>
    /// WhatsApp is the delivery channel because it is already the channel: these customers have been
    /// sending their orders over it, so the number is known to work and the message arrives where
    /// they are already looking — at no per-message cost. Blank disables WhatsApp delivery.
    /// </remarks>
    public string OtpWhatsAppSessionId { get; set; } = string.Empty;

    /// <summary>
    /// The message sent to the customer. <c>{code}</c> and <c>{minutes}</c> are substituted.
    /// </summary>
    public string OtpMessageTemplate { get; set; } =
        "Your Kefalos Orders code is {code}. It expires in {minutes} minutes. Do not share it with anyone.";

    /// <summary>
    /// Write generated codes to the log so sign-in can be exercised without a working gateway.
    /// </summary>
    /// <remarks>
    /// Honoured only when the host environment is Development, and false by default, because a
    /// production log containing live one-time codes would hand anyone who can read the log every
    /// customer account. Both conditions are required deliberately: a single flag is too easy to
    /// carry into an appsettings.Production.json by copy and paste.
    /// </remarks>
    public bool LogOtpCodesInDevelopment { get; set; }
}
