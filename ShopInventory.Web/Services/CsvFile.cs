using System.Text;
using Microsoft.JSInterop;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds a CSV and hands it to the browser as a download.
/// </summary>
/// <remarks>
/// One implementation rather than one per page. The escaping is the part worth sharing: a customer name
/// carrying a comma, a platform error carrying a quote or a newline, each silently shifts every later
/// column of a row if it is written raw — and a fiscal export that is quietly one column out is worse
/// than no export, because it still opens.
/// </remarks>
public static class CsvFile
{
    /// <summary>
    /// Quotes a field where it needs it, and never where it does not.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Both carriage-return forms become a plain newline first, so the quoted field a reader sees is
        // the one this method decided to quote rather than whatever the source system's line ending was.
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var escaped = normalized.Replace("\"", "\"\"");

        return escaped.IndexOfAny([',', '"', '\n']) >= 0
            ? $"\"{escaped}\""
            : escaped;
    }

    /// <summary>Escapes each field and joins them into one line.</summary>
    public static string Row(params string?[] fields) =>
        string.Join(',', fields.Select(Escape));

    /// <summary>
    /// Sends the text to the browser as a downloaded file.
    /// </summary>
    /// <remarks>
    /// With a UTF-8 byte-order mark, because Excel on Windows reads a BOM-less file as the system code
    /// page and renders every accented customer name wrong.
    /// </remarks>
    public static async Task DownloadAsync(IJSRuntime js, string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(content))
            .ToArray();

        await js.InvokeVoidAsync(
            "downloadFile",
            fileName,
            "text/csv;charset=utf-8",
            Convert.ToBase64String(bytes));
    }
}
