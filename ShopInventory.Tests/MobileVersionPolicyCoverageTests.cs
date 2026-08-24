using System.Text.Json;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Tests;

/// <summary>
/// Every app the version gate recognises must have a version policy of its own.
///
/// This exists because registering an app in <see cref="MobileVersionPolicyAppCatalog"/> and
/// stopping there is silently fatal. <c>MobileVersionPolicy</c> has a root-level policy that any app
/// without an <c>Apps</c> entry falls back to, and that root policy belongs to a different app with
/// a different version series. A newly registered app inherits it, fails the minimum-version check
/// on its very first request, and every install is told to update from Google Play - with a link to
/// the wrong app's store page.
///
/// Nothing about that shows up in a build or a test run. It was found by installing the app on a
/// handset and watching it refuse to sign in, which is a slow way to learn it.
/// </summary>
public sealed class MobileVersionPolicyCoverageTests
{
    /// <summary>
    /// The apps that knowingly share the root-level policy.
    /// </summary>
    /// <remarks>
    /// Both predate the per-app <c>Apps</c> section, and the root policy is theirs: its
    /// <c>GooglePlayUrl</c> points at com.shopinventory.mobile. They are listed rather than
    /// excluded by a rule so that the set is checked in both directions - a new app arriving
    /// without a policy grows this set and fails, and one of these gaining its own policy shrinks
    /// it and fails, which is the prompt to update the list.
    ///
    /// Sharing is not ideal even for these two: a Cheeseman handset told to update is sent to the
    /// SO app's store page. That is pre-existing and left alone here rather than changed blind.
    /// </remarks>
    private static readonly string[] SharesTheRootPolicy =
    [
        MobileVersionPolicyAppCatalog.CheesemanPolicyKey,
        MobileVersionPolicyAppCatalog.KefalosSalesOrderPolicyKey
    ];

    [Fact]
    public void Only_the_grandfathered_apps_fall_back_to_the_root_policy()
    {
        var apps = ReadConfiguredApps("appsettings.json");

        var withoutPolicy = MobileVersionPolicyAppCatalog.SupportedPolicyKeys
            .Where(key => !apps.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expected = SharesTheRootPolicy
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            withoutPolicy.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
            "Apps falling back to the root version policy have changed. "
            + $"Expected [{string.Join(", ", expected)}] but found [{string.Join(", ", withoutPolicy)}]. "
            + "An app added to the catalogue without a MobileVersionPolicy:Apps entry inherits "
            + "another app's version series, fails the minimum-version check on its first request, "
            + "and every install is told to update - with a link to the wrong app's store page.");
    }

    [Fact]
    public void The_development_config_covers_the_same_apps()
    {
        // Development overrides the whole MobileVersionPolicy section rather than merging into it,
        // so an app added to one file and not the other is blocked for anyone running locally.
        var production = ReadConfiguredApps("appsettings.json");
        var development = ReadConfiguredApps("appsettings.Development.json");

        var missing = production
            .Where(key => !development.Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These apps have a version policy in appsettings.json but not in "
            + $"appsettings.Development.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void No_app_is_configured_that_the_catalogue_does_not_know()
    {
        // The other direction. A policy under a key the catalogue cannot resolve is dead config: it
        // reads like the app is governed when nothing consults it.
        var apps = ReadConfiguredApps("appsettings.json");

        var unknown = apps
            .Where(key => !MobileVersionPolicyAppCatalog.IsSupportedPolicyKey(key))
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            $"These MobileVersionPolicy:Apps entries name no app the catalogue recognises, so "
            + $"nothing reads them: {string.Join(", ", unknown)}");
    }

    private static HashSet<string> ReadConfiguredApps(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "ShopInventory", fileName);
        Assert.True(File.Exists(path), $"{fileName} is not at {path}.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("MobileVersionPolicy", out var policy)
            || !policy.TryGetProperty("Apps", out var apps)
            || apps.ValueKind != JsonValueKind.Object)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return apps.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
