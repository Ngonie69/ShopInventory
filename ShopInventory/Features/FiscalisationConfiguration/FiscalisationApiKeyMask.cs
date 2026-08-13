namespace ShopInventory.Features.FiscalisationConfiguration;

/// <summary>
/// Renders a Fiscalisation API key for display.
/// </summary>
public static class FiscalisationApiKeyMask
{
    /// <summary>How many characters of the key are shown, when any are.</summary>
    private const int VisibleCharacters = 4;

    /// <summary>
    /// Below this the key is hidden outright: on a short key, four characters is a large enough share
    /// of it to be worth guarding, and a short key is a malformed one anyway.
    /// </summary>
    private const int MinimumLengthToRevealTail = 12;

    /// <summary>
    /// The key with all but its last few characters replaced, or null when there is no key.
    /// </summary>
    /// <remarks>
    /// The tail is there so an administrator can tell which key is installed — before a rotation, and
    /// after one — without the screen ever being able to hand the key back out. The length is not
    /// reproduced either, since that is a hint about the key itself.
    /// </remarks>
    public static string? Mask(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var key = apiKey.Trim();

        return key.Length < MinimumLengthToRevealTail
            ? "••••••••"
            : "••••••••" + key[^VisibleCharacters..];
    }
}
