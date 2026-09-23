using System.Text.RegularExpressions;

namespace ShopInventory.Tests;

/// <summary>
/// Holds every HttpClient the Web uses to reach the API to naming itself as the portal.
///
/// <para>
/// The API's maintenance lockout can only freeze the web portal if the portal says which audience
/// it is, and it says so with one header put on at registration. A client registered later without
/// it would be a hole in the lockout of exactly the worst kind: the settings screen would show the
/// portal ticked and frozen, and whichever pages happened to use that client would keep posting.
/// </para>
///
/// <para>
/// So rather than trusting a reviewer to notice, this reads <c>ShopInventory.Web/Program.cs</c> and
/// fails naming any <c>AddHttpClient</c> registration that points at the API and does not carry the
/// header. The cost of adding a genuine new client is one line there and nothing here.
/// </para>
/// </summary>
public sealed class WebApiClientHeaderTests
{
    [Fact]
    public void Every_api_client_the_web_registers_names_itself_as_the_portal()
    {
        var unmarked = ReadApiClientRegistrations()
            .Where(registration => !registration.Body.Contains("IdentifyAsWebPortal", StringComparison.Ordinal))
            .Select(registration => registration.Name)
            .ToList();

        Assert.True(
            unmarked.Count == 0,
            $"These API clients do not call WebApiClientIdentity.IdentifyAsWebPortal: {string.Join(", ", unmarked)}. "
            + "Calls made on them are invisible to the maintenance lockout, so freezing the web portal "
            + "would leave whatever uses them still transacting.");
    }

    [Fact]
    public void The_sweep_finds_the_registrations_at_all()
    {
        // Without this, moving or reshaping Program.cs would turn the test above into one that
        // passes by finding nothing — the failure mode of every test that reads the repository.
        var registrations = ReadApiClientRegistrations();

        Assert.True(
            registrations.Count >= 4,
            $"Only found {registrations.Count} API client registrations in the Web's Program.cs; the sweep is not reading it.");
    }

    private sealed record Registration(string Name, string Body);

    /// <summary>
    /// The <c>AddHttpClient</c> blocks that point at the API, with the body of each.
    /// </summary>
    /// <remarks>
    /// A registration counts as pointing at the API when its body sets <c>BaseAddress</c> from
    /// <c>apiBaseUrl</c>, which is how every one of them is written. Clients aimed anywhere else —
    /// there are none today — are left alone rather than being asserted about wrongly.
    /// </remarks>
    private static List<Registration> ReadApiClientRegistrations()
    {
        var source = ReadWebProgram();
        var registrations = new List<Registration>();

        foreach (Match match in Regex.Matches(
                     source,
                     @"AddHttpClient(?<generic><[^>]*>)?\(\s*(?<name>""[^""]*"")?",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)))
        {
            var body = ReadBalancedBlock(source, match.Index + match.Length);
            if (!body.Contains("apiBaseUrl", StringComparison.Ordinal))
            {
                continue;
            }

            var name = match.Groups["name"].Success
                ? match.Groups["name"].Value.Trim('"')
                : match.Groups["generic"].Value.Trim('<', '>');

            registrations.Add(new Registration(name, body));
        }

        return registrations;
    }

    /// <summary>
    /// From <paramref name="start"/> to the parenthesis that closes the call it is inside.
    /// </summary>
    private static string ReadBalancedBlock(string source, int start)
    {
        var depth = 1;
        for (var i = start; i < source.Length; i++)
        {
            depth += source[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return source[start..i];
            }
        }

        return source[start..];
    }

    private static string ReadWebProgram()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "ShopInventory.Web", "Program.cs");
        Assert.True(File.Exists(path), $"The Web's Program.cs is not at {path}.");

        return File.ReadAllText(path);
    }
}
