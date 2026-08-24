using System.Text;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// Turns a phone number as a customer would type it into the one form the account is keyed on.
/// </summary>
/// <remarks>
/// The number is the whole credential here, and it is also the unique key of
/// <c>VanSalesCustomerAccounts</c>. A shopkeeper typing <c>0771234567</c>, an operator pasting
/// <c>+263 77 123 4567</c> from a WhatsApp contact, and an import writing <c>263771234567</c> are
/// one person; without a single canonical form they are three rows, and the one who signs in is
/// whichever row happened to be created first.
/// <para>
/// Deliberately conservative: this normalises formatting and the local-vs-international prefix, and
/// nothing else. It does not check that the subscriber exists or that the operator prefix is real —
/// the OTP does that, by the number either receiving a message or not.
/// </para>
/// </remarks>
public static class VanSalesCustomerPhone
{
    /// <summary>Longest E.164 number: 15 digits plus the leading '+'.</summary>
    private const int MaxE164Length = 16;

    /// <summary>Shortest number worth attempting to send to, guarding against a stray digit or two.</summary>
    private const int MinDigits = 7;

    /// <summary>
    /// Normalise <paramref name="input"/> to E.164, applying <paramref name="defaultCountryCode"/>
    /// to a number written in local form.
    /// </summary>
    /// <returns><see langword="true"/> and the normalised value, or <see langword="false"/> if the
    /// input cannot be a phone number at all.</returns>
    public static bool TryNormalise(string? input, string defaultCountryCode, out string normalised)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();

        // Keep only digits, remembering whether the caller wrote an explicit '+' first. Spaces,
        // dashes, brackets and non-breaking spaces all arrive in pasted numbers.
        var digits = new StringBuilder(trimmed.Length);
        var hasPlus = trimmed[0] == '+';

        foreach (var c in trimmed)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
            else if (c != '+' && c != ' ' && c != '-' && c != '(' && c != ')' && c != '.' && c != ' ')
            {
                // A letter or symbol in the middle is not formatting; refuse rather than guess.
                return false;
            }
        }

        var value = digits.ToString();
        if (value.Length < MinDigits)
        {
            return false;
        }

        var country = NormaliseCountryCode(defaultCountryCode);

        string e164;
        if (hasPlus)
        {
            // Already international; the caller said so.
            e164 = "+" + value;
        }
        else if (value.StartsWith("00", StringComparison.Ordinal))
        {
            // The other way of writing international, common on printed stationery.
            e164 = "+" + value[2..].TrimStart('0');
        }
        else if (value.StartsWith('0'))
        {
            // Local trunk form: drop the national prefix and apply the country code.
            e164 = country + value.TrimStart('0');
        }
        else if (!string.IsNullOrEmpty(country) && value.StartsWith(country[1..], StringComparison.Ordinal))
        {
            // Country code present but the '+' was not typed.
            e164 = "+" + value;
        }
        else
        {
            // A bare subscriber number.
            e164 = country + value;
        }

        if (e164.Length is < MinDigits + 1 or > MaxE164Length)
        {
            return false;
        }

        normalised = e164;
        return true;
    }

    /// <summary>
    /// The last few digits, for showing an operator which number a code went to without reprinting
    /// the whole number in a log or on a screen.
    /// </summary>
    public static string Mask(string e164)
    {
        if (string.IsNullOrWhiteSpace(e164) || e164.Length <= 4)
        {
            return "****";
        }

        return string.Concat("*** *** ", e164.AsSpan(e164.Length - 4));
    }

    private static string NormaliseCountryCode(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        var digits = new string(configured.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? string.Empty : "+" + digits;
    }
}
