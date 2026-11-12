using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// The note added to a credit memo's Remarks when the app adds its approved draft: who raised the draft.
/// </summary>
/// <remarks>
/// The app adds the draft under its own SAP account, so the credit memo's creator in SAP is the service
/// user, not the person who keyed the return. The approval request names them (its originator); this
/// keeps that name on the document once the request is gone — SAP deletes it on the add.
/// </remarks>
public static class CreditNoteRaisedByRemark
{
    /// <summary>ORIN.Comments is a single 254-character column in SAP Business One.</summary>
    public const int MaxLength = 254;

    public const string Prefix = "Raised by ";

    private const string Separator = " | ";
    private const string Ellipsis = "…";

    /// <summary>"Raised by Jane Moyo (jmoyo)"; null when nobody can be named.</summary>
    public static string? Describe(SAPUser? user, int? userId)
    {
        var name = user?.UserName?.Trim();
        var code = user?.UserCode?.Trim();

        var who = (string.IsNullOrEmpty(name), string.IsNullOrEmpty(code)) switch
        {
            (false, false) when !string.Equals(name, code, StringComparison.OrdinalIgnoreCase) => $"{name} ({code})",
            (false, _) => name,
            (true, false) => code,
            _ => userId is int id ? $"SAP user {id}" : null
        };

        return who is null ? null : Prefix + who;
    }

    /// <summary>
    /// The remarks with <paramref name="raisedBy"/> after them, cut to fit the column by shortening the
    /// remarks, never the name. Null when the remarks already say who raised it, so a retry adds nothing.
    /// </summary>
    public static string? Append(string? comments, string raisedBy)
    {
        var existing = comments?.Trim() ?? string.Empty;
        if (existing.Contains(raisedBy, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (existing.Length == 0)
        {
            return raisedBy.Length <= MaxLength ? raisedBy : raisedBy[..MaxLength];
        }

        var combined = existing + Separator + raisedBy;
        if (combined.Length <= MaxLength)
        {
            return combined;
        }

        var room = MaxLength - Separator.Length - raisedBy.Length - Ellipsis.Length;
        return room > 0
            ? existing[..room].TrimEnd() + Ellipsis + Separator + raisedBy
            : raisedBy.Length <= MaxLength ? raisedBy : raisedBy[..MaxLength];
    }
}
