using System.Text.RegularExpressions;
using ShopInventory.Features.Maintenance;

namespace ShopInventory.Tests;

/// <summary>
/// Holds <see cref="MaintenanceGate.ReadOnlyPostRoutes"/> honest against the controllers.
///
/// <para>
/// The transaction lockout decides by verb, with that list as the exception: POSTs that are
/// searches because their filter is a body, and so must keep working while a driver is mid-round.
/// A hand-kept list of routes is exactly the thing that rots — a route gets renamed, or a handler
/// that used to answer a question starts filing something — and this one rots in the dangerous
/// direction, because every entry is a hole in the lockout.
/// </para>
///
/// <para>
/// So rather than trust the list, these tests re-derive it from the controllers on every run: each
/// entry must still be a POST route, and its action must still dispatch a query rather than a
/// command. The cost of adding a genuine new read-shaped POST is one line in the gate and nothing
/// here; the cost of an entry going stale is a failing test naming it.
/// </para>
/// </summary>
public sealed class MaintenanceRouteClassificationTests
{
    [Fact]
    public void Every_allowlisted_route_is_still_a_post_route_that_only_reads()
    {
        var actions = ReadControllerActions();

        foreach (var route in MaintenanceGate.ReadOnlyPostRoutes)
        {
            var match = actions.SingleOrDefault(action =>
                string.Equals(action.Route, route, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                match is not null,
                $"{route} is allowlisted as a read-only POST, but no controller exposes it as a POST any more. "
                + "Either fix the entry or drop it — while it is wrong, the maintenance lockout has a hole.");

            Assert.True(
                match!.DispatchesQueryOnly,
                $"{route} is allowlisted as a read-only POST, but {match.Controller}.{match.Action} now dispatches "
                + $"{string.Join(", ", match.Dispatched)}. A POST that changes something must not be exempt from "
                + "the maintenance lockout.");
        }
    }

    [Fact]
    public void The_allowlist_has_no_duplicates()
    {
        var duplicates = MaintenanceGate.ReadOnlyPostRoutes
            .GroupBy(route => route, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Listed more than once: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void The_sweep_finds_the_controllers_at_all()
    {
        // Without this, a move of the controllers would turn the test above into one that passes by
        // finding nothing — the failure mode of every test that reads the repository.
        var actions = ReadControllerActions();

        Assert.True(actions.Count > 100, $"Only found {actions.Count} POST actions; the sweep is not reading the controllers.");
    }

    private sealed record ControllerAction(
        string Controller,
        string Action,
        string Route,
        IReadOnlyList<string> Dispatched)
    {
        /// <summary>
        /// Whether everything this action sends through MediatR is a query.
        /// </summary>
        /// <remarks>
        /// An action that dispatches nothing recognisable counts as not-a-query. The naming
        /// convention is repo policy — <c>{Operation}Command</c> for writes, <c>{Operation}Query</c>
        /// for reads — so an action that follows neither is one nobody can classify by reading it,
        /// and it does not belong on an allowlist.
        /// </remarks>
        public bool DispatchesQueryOnly =>
            Dispatched.Count > 0 && Dispatched.All(type => type.EndsWith("Query", StringComparison.Ordinal));
    }

    private static readonly Regex ControllerRoute =
        new(@"\[Route\(""(?<route>[^""]+)""\)\]", RegexOptions.Compiled);

    /// <summary>
    /// An <c>[HttpPost("…")]</c>, its action, and the MediatR requests the action constructs.
    /// </summary>
    /// <remarks>
    /// The body is taken as everything up to the next attribute-led member or the end of the file,
    /// which is enough to see the <c>new SomethingQuery(</c> it sends without parsing C#.
    /// </remarks>
    private static readonly Regex PostAction = new(
        @"\[HttpPost\(""(?<path>[^""]*)""\)\][\s\S]{0,800}?public\s+(?:static\s+)?(?:async\s+)?[^\n]*?\s(?<action>\w+)\s*\([\s\S]*?(?=\n    \[Http|\n    /// <summary>|\Z)",
        RegexOptions.Compiled);

    private static readonly Regex Dispatched =
        new(@"new\s+(?<request>\w+(?:Query|Command))\s*[\(\{]", RegexOptions.Compiled);

    private static List<ControllerAction> ReadControllerActions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var controllersPath = Path.Combine(directory!.FullName, "ShopInventory", "Controllers");
        Assert.True(Directory.Exists(controllersPath), $"No controllers at {controllersPath}.");

        var actions = new List<ControllerAction>();

        foreach (var file in Directory.EnumerateFiles(controllersPath, "*Controller.cs"))
        {
            var source = File.ReadAllText(file);
            var controller = Path.GetFileNameWithoutExtension(file);

            var routeMatch = ControllerRoute.Match(source);
            if (!routeMatch.Success)
            {
                continue;
            }

            var basePath = routeMatch.Groups["route"].Value
                .Replace("[controller]", controller.Replace("Controller", string.Empty), StringComparison.Ordinal);

            foreach (Match action in PostAction.Matches(source))
            {
                var path = action.Groups["path"].Value;
                var route = "/" + string.Join(
                    '/',
                    (basePath + "/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries));

                actions.Add(new ControllerAction(
                    controller,
                    action.Groups["action"].Value,
                    NormalizeRouteConstraints(route),
                    [.. Dispatched.Matches(action.Value).Select(match => match.Groups["request"].Value).Distinct(StringComparer.Ordinal)]));
            }
        }

        return actions;
    }

    /// <summary>
    /// Strips route constraints, so <c>{docEntry:int}</c> compares equal to <c>{docEntry}</c>.
    /// </summary>
    private static string NormalizeRouteConstraints(string route) =>
        Regex.Replace(route, @"\{(?<name>\w+)(?::[^}]+)?\??\}", "{${name}}");
}
