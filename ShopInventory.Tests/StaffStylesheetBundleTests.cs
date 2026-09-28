using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the conditions under which bundling the staff page stylesheets leaves the cascade unchanged.
/// </summary>
/// <remarks>
/// StaffStylesheets.targets concatenates the staff pages' sheets, in the order App.razor used to link
/// them, into two files. Rules from sheets linked one after another cascade exactly as the same rules
/// written one after another in a single file, provided nothing in a sheet depends on it being its own
/// file: an @import or @charset is only honoured at the top of a file, a relative url() resolves
/// against the file's own folder, and an unclosed brace or comment would swallow the next sheet.
/// </remarks>
public sealed class StaffStylesheetBundleTests
{
    private static readonly string Web = Path.Combine(FindRepoRoot(), "ShopInventory.Web");
    private static readonly string[] Bundles = ["staff-early.bundle.css", "staff-late.bundle.css"];

    public static TheoryData<string> ListedSheets()
    {
        var data = new TheoryData<string>();
        foreach (var sheet in Listed().SelectMany(list => list.Sheets))
            data.Add(sheet);
        return data;
    }

    [Theory]
    [MemberData(nameof(ListedSheets))]
    public void A_listed_sheet_means_the_same_inside_a_bundle(string sheet)
    {
        Assert.StartsWith("wwwroot/css/", sheet);
        var bytes = File.ReadAllBytes(Path.Combine(Web, sheet));
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{sheet} starts with a byte-order mark.");

        var css = Encoding.UTF8.GetString(bytes);
        foreach (var rule in new[] { "@import", "@charset", "@namespace" })
            Assert.DoesNotContain(rule, css);

        var relativeUrls = Regex.Matches(css, @"url\(\s*['""]?([^'"")]+)")
            .Select(match => match.Groups[1].Value)
            .Where(url => !url.StartsWith("data:") && !url.StartsWith("http") && !url.StartsWith('/') && !url.StartsWith('#'))
            .ToList();
        Assert.Empty(relativeUrls);

        var withoutComments = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotContain("/*", withoutComments);
        Assert.Equal(withoutComments.Count(c => c == '{'), withoutComments.Count(c => c == '}'));
    }

    [Fact]
    public void App_razor_links_each_bundle_once_and_none_of_the_bundled_sheets()
    {
        var app = File.ReadAllText(Path.Combine(Web, "Components", "App.razor"));

        foreach (var bundle in Bundles)
            Assert.Single(Regex.Matches(app, Regex.Escape($"@Assets[\"css/{bundle}\"]")));

        // Linked as well as bundled would put the sheet's rules in the cascade twice, at two places.
        foreach (var sheet in Listed().SelectMany(list => list.Sheets))
            Assert.DoesNotContain($"@Assets[\"{sheet["wwwroot/".Length..]}\"]", app);
    }

    [Fact]
    public void The_build_wrote_each_bundle_as_its_sheets_in_order()
    {
        // The test project references the Web project, so its build has run the bundling target.
        foreach (var (list, bundle) in Listed().Zip(Bundles))
        {
            var expected = new StringBuilder();
            foreach (var sheet in list.Sheets)
            {
                expected.Append("/* ").Append(sheet).Append(" */\n");
                expected.Append(File.ReadAllText(Path.Combine(Web, sheet)));
                expected.Append('\n');
            }

            Assert.Equal(expected.ToString(), File.ReadAllText(Path.Combine(Web, "wwwroot", "css", bundle)));
        }
    }

    private static List<(string Item, List<string> Sheets)> Listed()
    {
        var project = XDocument.Load(Path.Combine(Web, "StaffStylesheets.targets"));
        return new[] { "StaffStylesheetEarly", "StaffStylesheetLate" }
            .Select(item => (item, project.Descendants(item).Select(e => (string)e.Attribute("Include")!).ToList()))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShopInventory.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find ShopInventory.sln above the test binaries.");
    }
}
