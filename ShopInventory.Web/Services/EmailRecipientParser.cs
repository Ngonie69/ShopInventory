using System.Text;
using MimeKit;

namespace ShopInventory.Web.Services;

/// <summary>
/// Parses recipient lists containing bare addresses or RFC-style mailboxes such as
/// <c>Jane Smith &lt;jane@example.com&gt;</c>. Commas, semicolons and new lines are accepted
/// as separators, while separators inside quoted display names or angle brackets are preserved.
/// </summary>
internal static class EmailRecipientParser
{
    public static List<string> Parse(string? value) =>
        ParseMailboxes(string.IsNullOrWhiteSpace(value) ? [] : [value])
            .Select(mailbox => mailbox.ToString())
            .ToList();

    public static List<MailboxAddress> ParseMailboxes(IEnumerable<string>? values)
    {
        var mailboxes = new List<MailboxAddress>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values ?? Enumerable.Empty<string>())
        {
            foreach (var token in SplitRecipientList(value))
            {
                if (!TryParseMailbox(token, out var mailbox) ||
                    !seenAddresses.Add(mailbox.Address))
                {
                    continue;
                }

                mailboxes.Add(mailbox);
            }
        }

        return mailboxes;
    }

    private static bool TryParseMailbox(string token, out MailboxAddress mailbox)
    {
        mailbox = null!;
        var trimmed = token.Trim();

        // MimeKit supports RFC double-quoted display names. Also accept the commonly pasted
        // Outlook-style variant that wraps the display name in single quotes.
        var angleStart = trimmed.LastIndexOf('<');
        if (angleStart > 0 && trimmed.EndsWith('>'))
        {
            var displayName = trimmed[..angleStart].Trim();
            if (displayName.Length >= 2 && displayName[0] == '\'' && displayName[^1] == '\'')
            {
                var addressText = trimmed[(angleStart + 1)..^1].Trim();
                if (MailboxAddress.TryParse(addressText, out var addressOnly))
                {
                    mailbox = new MailboxAddress(displayName[1..^1].Trim(), addressOnly.Address);
                    return true;
                }
            }
        }

        if (MailboxAddress.TryParse(trimmed, out var parsedMailbox))
        {
            mailbox = parsedMailbox;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitRecipientList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var token = new StringBuilder();
        var quote = '\0';
        var insideAngleBrackets = false;
        var escaped = false;

        foreach (var character in value)
        {
            if (escaped)
            {
                token.Append(character);
                escaped = false;
                continue;
            }

            if (quote != '\0')
            {
                token.Append(character);
                if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character == '"' || (character == '\'' && string.IsNullOrWhiteSpace(token.ToString())))
            {
                quote = character;
                token.Append(character);
                continue;
            }

            if (character == '<')
            {
                insideAngleBrackets = true;
                token.Append(character);
                continue;
            }

            if (character == '>')
            {
                insideAngleBrackets = false;
                token.Append(character);
                continue;
            }

            if (!insideAngleBrackets && character is ',' or ';' or '\r' or '\n')
            {
                var parsedToken = token.ToString().Trim();
                if (parsedToken.Length > 0)
                {
                    yield return parsedToken;
                }

                token.Clear();
                continue;
            }

            token.Append(character);
        }

        var finalToken = token.ToString().Trim();
        if (finalToken.Length > 0)
        {
            yield return finalToken;
        }
    }
}
