using System.Text.RegularExpressions;

namespace ShopInventory.Tests;

/// <summary>
/// Guards against a URL literal in the SAP client that names a <c>$select</c> constant but is not
/// interpolated.
/// </summary>
/// <remarks>
/// <c>"Orders?{QuotationSelect}"</c> compiles perfectly happily and sends the braces to SAP
/// verbatim. Three URLs were built exactly that way when the select constants were introduced by a
/// script editing literals that had not previously needed interpolation. The compiler was silent,
/// and the tests around the constants checked the constants rather than the URLs using them.
///
/// Scoped to the select constants rather than any <c>{Placeholder}</c>: structured logging
/// templates use braces in ordinary literals all over this file and are entirely correct.
/// </remarks>
public class SapUrlLiteralTests
{
    private const string ClientPath = "ShopInventory/Services/SAPServiceLayerClient.cs";

    [Fact]
    public void Select_constants_are_only_used_inside_interpolated_strings()
    {
        var source = File.ReadAllLines(Path.Combine(RepositoryRoot(), ClientPath));

        var selectConstants = source
            .Select(line => Regex.Match(line, @"private const string (\w*Select\w*)\s*="))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(selectConstants);

        var uninterpolated = new Regex(
            @"(?<!\$)""[^""]*\{(" + string.Join('|', selectConstants) + @")\}");

        var offenders = source
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(entry => uninterpolated.IsMatch(entry.Line))
            .Select(entry => $"{ClientPath}:{entry.Number}: {entry.Line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These literals name a $select constant but have no $ prefix, so the braces reach SAP verbatim:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
