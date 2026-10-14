using System.Text;

#if SHOPINVENTORY_WEB
namespace ShopInventory.Web.Common.Security;
#else
namespace ShopInventory.Common.Security;
#endif

/// <summary>
/// What is known about the person a password is being set for.
/// </summary>
/// <remarks>
/// Everything is optional: a customer has no username, a till account often has no email. Whatever
/// is here must not turn up inside the password.
/// </remarks>
public sealed record PasswordOwner
{
    public string? Username { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? PhoneNumber { get; init; }

    /// <summary>
    /// Anything else that identifies them and is easy to find out — an account code, a shop name.
    /// </summary>
    public IReadOnlyList<string?> Other { get; init; } = [];
}

/// <summary>
/// The company password policy, as far as a password can be judged by looking at it.
/// </summary>
/// <remarks>
/// <para>
/// Three of the policy's rules are decided here, and they are the same for every kind of account —
/// staff, portal customers and the shops on the ordering app:
/// </para>
/// <list type="number">
/// <item>at least <see cref="MinimumLength"/> characters;</item>
/// <item>nothing of the owner's name, username or other easily found detail inside it;</item>
/// <item>not a common password.</item>
/// </list>
/// <para>
/// The rest of the policy needs more than the password to decide and lives elsewhere: reuse and the
/// three-month renewal are properties of an account's history, so they belong to whoever owns that
/// history.
/// </para>
/// <para>
/// One file, compiled into both the API and the Web. The portal's accounts never pass through the
/// API, so a rule kept on one side only would be a rule half the passwords never met.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>Shortest password accepted anywhere.</summary>
    public const int MinimumLength = 8;

    public const string TooShort = "Use at least 8 characters.";

    public const string TooCommon =
        "That password is too common or too easy to guess. Choose one nobody would try.";

    public const string ContainsPersonalDetail =
        "A password must not contain your name, username, email address or phone number.";

    /// <summary>
    /// Shortest piece of a name that counts. Below this, an ordinary word contains it by accident
    /// — "Jo" is inside "major" — and the refusal would be impossible to explain.
    /// </summary>
    private const int ShortestPersonalToken = 3;

    /// <summary>A run of digits shorter than this is a house number, not a phone number.</summary>
    private const int ShortestNumericToken = 5;

    /// <summary>
    /// Every rule <paramref name="password"/> breaks, as a sentence fit to show the person typing
    /// it. Empty when it is acceptable.
    /// </summary>
    public static IReadOnlyList<string> Check(string? password, PasswordOwner? owner = null)
    {
        var broken = new List<string>(3);
        var value = password ?? string.Empty;

        if (value.Length < MinimumLength)
        {
            broken.Add(TooShort);
        }

        // A password too short to keep is not also lectured about being common.
        if (broken.Count == 0 && IsCommon(value))
        {
            broken.Add(TooCommon);
        }

        if (owner is not null && value.Length > 0 && ContainsDetailOf(value, owner))
        {
            broken.Add(ContainsPersonalDetail);
        }

        return broken;
    }

    /// <summary>
    /// Whether this is one of the passwords an attacker tries before thinking.
    /// </summary>
    /// <remarks>
    /// Three kinds are caught. A pattern with no information in it — one character repeated, a short
    /// block repeated, a straight run up or down the digits or the alphabet. A word off
    /// <see cref="CommonPasswords"/>. And that same word dressed up: capitals, the digits and
    /// punctuation hung on either end to satisfy a length rule, and the letter swaps (<c>@</c> for
    /// <c>a</c>, <c>0</c> for <c>o</c>) that every cracking tool undoes first.
    /// </remarks>
    public static bool IsCommon(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var lower = password.ToLowerInvariant();

        if (IsRepeatedBlock(lower) || IsStraightRun(lower))
        {
            return true;
        }

        // Peel the padding. Every way of dropping a run of non-letters from the front and from the
        // back is tried, because a swapped letter can sit at the edge: "h3ll0123" is "hello" only
        // once "123" has gone and the "0" has stayed.
        var leading = 0;
        while (leading < lower.Length && !char.IsLetter(lower[leading]))
        {
            leading++;
        }

        // Counted on its own, not from where the leading run stopped: a password with no letters at
        // all is padding from both ends, and "12345699" hides its run at the front.
        var trailing = 0;
        while (trailing < lower.Length && !char.IsLetter(lower[lower.Length - 1 - trailing]))
        {
            trailing++;
        }

        for (var start = 0; start <= leading; start++)
        {
            for (var end = lower.Length; end >= lower.Length - trailing && end > start; end--)
            {
                var candidate = lower[start..end];
                if (CommonPasswords.Stems.Contains(candidate) ||
                    CommonPasswords.Stems.Contains(UndoSwaps(candidate, oneIs: 'l')) ||
                    CommonPasswords.Stems.Contains(UndoSwaps(candidate, oneIs: 'i')))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the password has a piece of the owner's identity in it.
    /// </summary>
    public static bool ContainsDetailOf(string password, PasswordOwner owner)
    {
        var lower = password.ToLowerInvariant();
        var unswappedL = UndoSwaps(lower, oneIs: 'l');
        var unswappedI = UndoSwaps(lower, oneIs: 'i');

        foreach (var token in TokensOf(owner))
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }

            // A phone number is looked for as typed only: undoing swaps turns every digit in the
            // password into a letter, which is the opposite of what a number needs.
            if (!IsDigits(token) &&
                (unswappedL.Contains(token, StringComparison.Ordinal) ||
                 unswappedI.Contains(token, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> TokensOf(PasswordOwner owner)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        AddWords(tokens, owner.Username);
        AddWords(tokens, owner.FirstName);
        AddWords(tokens, owner.LastName);

        // The mailbox, not the domain: everybody at the company shares the domain, and the company's
        // own name is refused as a common password already.
        var email = owner.Email?.Trim();
        if (!string.IsNullOrEmpty(email))
        {
            var at = email.IndexOf('@');
            AddWords(tokens, at > 0 ? email[..at] : email);
        }

        foreach (var other in owner.Other)
        {
            AddWords(tokens, other);
        }

        // A phone number is written with or without its country code and its leading zero, so the
        // subscriber's own digits are what is looked for.
        var digits = DigitsOf(owner.PhoneNumber);
        if (digits.Length >= ShortestNumericToken)
        {
            tokens.Add(digits);
            if (digits.Length > 9)
            {
                tokens.Add(digits[^9..]);
            }

            if (digits.Length > 7)
            {
                tokens.Add(digits[^7..]);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Adds a value whole and in its parts: "t.moyo" is looked for as "tmoyo" and as "moyo".
    /// </summary>
    private static void AddWords(HashSet<string> tokens, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var whole = new StringBuilder(value.Length);
        var part = new StringBuilder(value.Length);

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                whole.Append(character);
                part.Append(character);
                continue;
            }

            AddToken(tokens, part.ToString());
            part.Clear();
        }

        AddToken(tokens, part.ToString());
        AddToken(tokens, whole.ToString());
    }

    private static void AddToken(HashSet<string> tokens, string token)
    {
        var shortest = IsDigits(token) ? ShortestNumericToken : ShortestPersonalToken;
        if (token.Length >= shortest)
        {
            tokens.Add(token);
        }
    }

    private static bool IsDigits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static string DigitsOf(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string(value.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// "aaaaaaaa", "12121212", "abcabcabc": one block of up to four characters, over and over.
    /// </summary>
    private static bool IsRepeatedBlock(string value)
    {
        for (var block = 1; block <= 4 && block * 2 <= value.Length; block++)
        {
            if (value.Length % block != 0)
            {
                continue;
            }

            var repeats = true;
            for (var index = block; index < value.Length && repeats; index++)
            {
                repeats = value[index] == value[index - block];
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "12345678", "87654321", "abcdefgh": each character one step on from the last, all the way.
    /// </summary>
    private static bool IsStraightRun(string value)
    {
        if (value.Length < 2)
        {
            return false;
        }

        var allDigits = value.All(char.IsAsciiDigit);
        var allLetters = value.All(char.IsAsciiLetter);
        if (!allDigits && !allLetters)
        {
            return false;
        }

        // Digits wrap, because the top row of a keyboard does: 7890123.
        var cycle = allDigits ? 10 : 0;
        return Steps(value, +1, cycle) || Steps(value, -1, cycle);
    }

    private static bool Steps(string value, int step, int cycle)
    {
        for (var index = 1; index < value.Length; index++)
        {
            var difference = value[index] - value[index - 1];
            if (difference == step)
            {
                continue;
            }

            if (cycle != 0 && difference == step - (cycle * step))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts back the letters people replace with look-alikes.
    /// </summary>
    /// <param name="value">Lower case already.</param>
    /// <param name="oneIs">What a <c>1</c> stands for. It is used for both <c>l</c> and <c>i</c>.</param>
    private static string UndoSwaps(string value, char oneIs)
    {
        var letters = new char[value.Length];

        for (var index = 0; index < value.Length; index++)
        {
            letters[index] = value[index] switch
            {
                '0' => 'o',
                '1' => oneIs,
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                '@' => 'a',
                '$' => 's',
                '!' => 'i',
                var other => other
            };
        }

        return new string(letters);
    }
}
