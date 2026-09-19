using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Components.Layout;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The staff sidebar, rendered for real once per role and compared with <c>NavMenu.roles.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// NavMenu is some fifty links under nested <c>AuthorizeView</c>s, each with its own role list, inside
/// section gates that must be the union of the rows beneath them. Moving a row between sections moves
/// it between gates, and nothing about a clean build says whether a role gained a link to a page that
/// refuses it, lost one it needs, or was left looking at a heading with nothing under it.
/// </para>
/// <para>
/// So the whole menu is pinned: every role, every heading it sees, every link under it, in order. A
/// change to the sidebar is a change to that file, and the diff is the review. On a mismatch the
/// rendered menu is written beside it as <c>NavMenu.roles.actual.txt</c>; if the difference is the
/// intended one, copy it over.
/// </para>
/// </remarks>
public sealed class NavMenuRoleRenderTests
{
    [Fact]
    public async Task Every_role_sees_the_pinned_sidebar()
    {
        var actual = await RenderAllRolesAsync();
        var expectedPath = SiblingPath("NavMenu.roles.txt");
        var actualPath = SiblingPath("NavMenu.roles.actual.txt");

        var expected = File.Exists(expectedPath) ? Normalise(await File.ReadAllTextAsync(expectedPath)) : "";
        if (expected != actual)
        {
            await File.WriteAllTextAsync(actualPath, actual);
            Assert.Fail($"The sidebar no longer matches {expectedPath}. The rendered menu is in {actualPath}.");
        }

        if (File.Exists(actualPath))
        {
            File.Delete(actualPath);
        }
    }

    [Fact]
    public async Task No_role_sees_an_empty_heading_or_the_same_page_twice()
    {
        foreach (var role in UserRoles.AllRoles)
        {
            var sections = Parse(await RenderAsync(role));

            foreach (var (title, links) in sections)
            {
                Assert.True(links.Count > 0, $"{role} sees the heading '{title}' with nothing under it.");
            }

            var duplicates = sections
                .SelectMany(s => s.Links)
                .GroupBy(l => l.Href)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.True(duplicates.Count == 0, $"{role} sees {string.Join(", ", duplicates)} more than once.");
        }
    }

    private static async Task<string> RenderAllRolesAsync()
    {
        var text = new StringBuilder();
        foreach (var role in UserRoles.AllRoles)
        {
            text.Append("## ").Append(role).Append('\n');
            foreach (var (title, links) in Parse(await RenderAsync(role)))
            {
                text.Append(title).Append('\n');
                foreach (var (label, href) in links)
                {
                    text.Append("    ").Append(label).Append("  ").Append(href).Append('\n');
                }
            }

            text.Append('\n');
        }

        return Normalise(text.ToString());
    }

    private static readonly Regex Token = new(
        """<span class="nav-section-text">(?<section>[^<]*)</span>|<a\b[^>]*\bhref="(?<href>[^"]*)"[^>]*>.*?<span class="snav-text">(?<label>[^<]*)</span>""",
        RegexOptions.Singleline);

    private static List<(string Title, List<(string Label, string Href)> Links)> Parse(string html)
    {
        var sections = new List<(string, List<(string, string)>)>();
        foreach (Match m in Token.Matches(html))
        {
            if (m.Groups["section"].Success)
            {
                sections.Add((WebUtility.HtmlDecode(m.Groups["section"].Value), []));
            }
            else
            {
                sections[^1].Item2.Add((WebUtility.HtmlDecode(m.Groups["label"].Value), m.Groups["href"].Value));
            }
        }

        return sections;
    }

    private static async Task<string> RenderAsync(string role)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<NavigationManager, FixedNavigationManager>();
        services.AddSingleton(StubProxy.For<ILocalStorageService>((_, _) => throw new NotSupportedException()));
        services.AddSingleton<NavSectionState>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "nav-test"), new Claim(ClaimTypes.Role, role)],
            authenticationType: "test"));
        var state = Task.FromResult(new AuthenticationState(user));

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment menu = builder =>
            {
                builder.OpenComponent<NavMenu>(0);
                builder.CloseComponent();
            };

            var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CascadingValue<object>.Value)] = state,
                    [nameof(CascadingValue<object>.ChildContent)] = menu
                }));

            return output.ToHtmlString();
        });
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n");

    private static string SiblingPath(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, name);

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager() => Initialize("https://shop.test/", "https://shop.test/");

        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
