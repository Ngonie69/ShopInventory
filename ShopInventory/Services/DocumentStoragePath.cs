using System.Text.RegularExpressions;

namespace ShopInventory.Services;

/// <summary>
/// Builds the on-disk paths for generated documents and uploaded attachments, from values the
/// server has checked, and proves each one lands inside the upload root.
/// </summary>
/// <remarks>
/// Both paths used to be joined straight from request fields. <c>Path.Combine</c> discards
/// everything before a rooted argument, so a document type of <c>C:\…</c> or <c>\\host\share\…</c>
/// made the API write wherever it was told, or reach out to another machine over SMB with its own
/// credentials; <c>..</c> segments climbed out of the root just as well. A request value now only
/// ever chooses between names the server already knows, and the final full path is checked against
/// the root whatever built it.
/// </remarks>
public static partial class DocumentStoragePath
{
    /// <summary>
    /// The document types a template can be written for, as the Document Templates page offers them.
    /// Generation names its file after one of these, never after the request's spelling.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownDocumentTypes =
        ["Invoice", "Quote", "SalesOrder", "PurchaseOrder", "DeliveryNote", "CreditNote"];

    /// <summary>The known type the request means, in its canonical spelling; null if none.</summary>
    public static string? CanonicalDocumentType(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return null;

        var trimmed = documentType.Trim();
        return KnownDocumentTypes.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether an attachment entity type can be used as a folder name: a plain identifier, so no
    /// separator, drive, dot segment or reserved character can reach the file system. The set of
    /// entity types is open (each feature names its own), so this checks the shape, not a list.
    /// </summary>
    public static bool IsSafeEntityType(string? entityType) =>
        !string.IsNullOrEmpty(entityType) && SafeSegment().IsMatch(entityType);

    /// <summary>
    /// An uploaded file's extension if it is a plain one (letters and digits, at most ten), or empty.
    /// The rest of the stored name is a GUID, so this is the only part the client chooses.
    /// </summary>
    public static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return SafeExtensionPattern().IsMatch(extension) ? extension : string.Empty;
    }

    /// <summary>
    /// <paramref name="root"/> joined with <paramref name="segments"/>, fully resolved, and refused
    /// unless it is strictly inside the root.
    /// </summary>
    public static string Within(string root, params string[] segments)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));

        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The requested file location is outside document storage.");
        }

        return fullPath;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex SafeSegment();

    [GeneratedRegex(@"^\.[A-Za-z0-9]{1,10}$")]
    private static partial Regex SafeExtensionPattern();
}
