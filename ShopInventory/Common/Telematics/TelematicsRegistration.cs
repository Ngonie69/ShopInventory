namespace ShopInventory.Common.Telematics;

/// <summary>
/// One spelling of a vehicle registration, so a plate typed here and a plate typed at Cartrack
/// are the same string.
/// </summary>
/// <remarks>
/// <para>
/// A registration reaches this system three ways and is free text in all of them: an
/// administrator types it onto a route, a rep confirms or overrides it on the handset at
/// departure, and the fleet provider spells it their own way in their own console. "AHF0218", "AHF 0218", "ahf-0218" and "AHF0218 " are one truck to a reader
/// and four to a join — and a join that misses reports the van as having never moved, which is
/// indistinguishable from the van having never moved. That is the failure this prevents: the
/// route entity's own class comment already warns about exactly this fragmentation for route
/// names, and nothing was mitigating it for plates.
/// </para>
/// <para>
/// Telematics tables store <em>only</em> the normalised form. The two existing columns —
/// <c>Routes.TruckRegNo</c> and <c>VanRouteDays.TruckRegNo</c> — are deliberately left exactly as
/// typed: they are what a person entered and what the day's sheet said, and rewriting them would
/// destroy that record to save a call to this method. Normalising in memory at the join is enough,
/// and it keeps one source of truth rather than a persisted copy that can drift.
/// </para>
/// </remarks>
public static class TelematicsRegistration
{
    /// <summary>
    /// The comparer to use for any dictionary or set keyed on a normalised registration. Ordinal:
    /// the value is already upper-invariant, so a culture-aware comparison would only add the
    /// Turkish-i class of surprise to a string that cannot contain a letter i problem.
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.Ordinal;

    /// <summary>
    /// Reduces a registration to the form the telematics tables key on: upper case, with every
    /// character that is not a letter or a digit removed. Blank in, null out — a route with no
    /// truck assigned and a route whose truck is a space are the same thing, and neither should
    /// produce a key that matches another blank.
    /// </summary>
    public static string? Normalize(string? registration)
    {
        if (string.IsNullOrWhiteSpace(registration))
        {
            return null;
        }

        Span<char> buffer = registration.Length <= 64
            ? stackalloc char[registration.Length]
            : new char[registration.Length];

        var length = 0;

        foreach (var character in registration)
        {
            // Deliberately ASCII-only. A plate is A-Z and 0-9 by construction, so anything else is
            // punctuation, whitespace, or a character that got in by accident — and silently
            // folding an accented letter into a plate would invent a match rather than find one.
            if (character is >= '0' and <= '9')
            {
                buffer[length++] = character;
            }
            else if (character is >= 'A' and <= 'Z')
            {
                buffer[length++] = character;
            }
            else if (character is >= 'a' and <= 'z')
            {
                buffer[length++] = (char)(character - 32);
            }
        }

        return length == 0 ? null : new string(buffer[..length]);
    }

    /// <summary>
    /// Whether two registrations name the same vehicle, whatever either was typed as.
    /// </summary>
    public static bool AreSame(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);

        return normalizedLeft is not null && normalizedLeft == Normalize(right);
    }
}
