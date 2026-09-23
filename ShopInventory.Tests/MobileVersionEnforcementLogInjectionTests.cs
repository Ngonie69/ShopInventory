using Microsoft.AspNetCore.Http;
using ShopInventory.Features.AppVersion;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// A request refused by the version gate is logged with the headers it arrived with, and those come
/// straight off the wire. A caller putting CR/LF in <c>X-App-Id</c>, <c>X-App-Platform</c>,
/// <c>X-App-Version</c> or <c>X-Device-Model</c> could otherwise write whole entries of their own
/// into the log somebody opens when handsets stop working. CodeQL flagged the same class in
/// <c>MaintenanceMiddleware</c> (PRs #562 and #563); this covers the three call sites here.
/// </summary>
/// <remarks>
/// <para>
/// Each test asserts the invariant that matters: <em>one refused request writes exactly one line</em>.
/// Asserting the attacker's text is absent would be wrong — the sanitiser deliberately keeps it, so
/// a reader can see what was sent — it just cannot start a line.
/// </para>
/// <para>
/// Every value on every branch is pinned: removing <c>SanitizeIdentifierForLog</c> from any one of
/// the twelve fails a test here. That is not free for the path, which needs an assertion of its own
/// — see <see cref="AssertThePathWentThroughTheSanitiser"/>.
/// </para>
/// </remarks>
public sealed class MobileVersionEnforcementLogInjectionTests
{
    // Short on purpose. The sanitiser caps an identifier at 64 characters, and a longer forged line
    // would be cut off there — which is safe, but then the tests would be proving the length cap
    // rather than that a kept value cannot start a line.
    private const string Forged = "[INF] Handled LoginCommand";

    private const string Path = "/api/vansales/sales";

    [Fact]
    public async Task Invalid_version_metadata_cannot_be_logged_as_its_own_entry()
    {
        var headers = new Dictionary<string, string>
        {
            // Trimmed to "android", so this still reads as an Android handset and reaches the
            // branch below — while the value that gets logged is the untrimmed one with the line
            // breaks still in it.
            ["X-App-Platform"] = "\r\n android \r\n",
            ["X-App-Id"] = "com.kefalos.vansales\r\n" + Forged,
            ["X-App-Version"] = "not-a-version\n" + Forged
        };

        var result = await InvokeAsync(
            Evaluation(hasValidVersionMetadata: false, requireHeaders: true),
            headers,
            path: Path + "\n" + Forged);

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var logged = AssertOneUnforgeableLine(result.Logged, stillReadable: "com.kefalos.vansales");
        AssertThePathWentThroughTheSanitiser(logged);
    }

    [Fact]
    public async Task A_blocked_version_cannot_be_logged_as_its_own_entry()
    {
        // The version reaches this line through the evaluator, which only trims it — an interior
        // newline survives Trim() untouched, so it is no safer than the raw header.
        var headers = new Dictionary<string, string>
        {
            ["X-App-Platform"] = "android",
            ["X-App-Id"] = "com.kefalos.vansales\r" + Forged,
            ["X-App-Version"] = "1.0.0\n" + Forged
        };

        var result = await InvokeAsync(
            Evaluation(shouldForceUpgrade: true, currentVersion: headers["X-App-Version"]),
            headers,
            path: Path + "\r\n" + Forged);

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        var logged = AssertOneUnforgeableLine(result.Logged, stillReadable: "1.0.0");
        AssertThePathWentThroughTheSanitiser(logged);
    }

    [Fact]
    public async Task Incomplete_metadata_on_an_unlabelled_request_cannot_be_logged_as_its_own_entry()
    {
        // This branch is only reached when the platform header is absent or blank, which does not
        // put it out of reach: IsNullOrWhiteSpace is true for a bare "\r\n", so a platform header
        // made only of line breaks clears the guard above and still reaches the log.
        var headers = new Dictionary<string, string>
        {
            ["X-App-Platform"] = "\r\n",
            ["X-App-Id"] = "com.kefalos.vansales\n" + Forged,
            ["X-App-Version"] = "2.0.1\r\n" + Forged,
            ["X-Device-Model"] = "Pixel 7\r\n" + Forged
        };

        var result = await InvokeAsync(
            Evaluation(requireHeaders: true),
            headers,
            path: Path + "\n" + Forged);

        Assert.False(result.ReachedTheApi);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var logged = AssertOneUnforgeableLine(result.Logged, stillReadable: "Pixel 7");
        AssertThePathWentThroughTheSanitiser(logged);
    }

    [Fact]
    public async Task An_ordinary_refusal_still_names_what_the_handset_sent()
    {
        // The sanitiser must not turn a legitimate refusal into an unreadable one: everything a
        // support call would be traced by has to survive untouched.
        var headers = new Dictionary<string, string>
        {
            ["X-App-Platform"] = "android",
            ["X-App-Id"] = "com.kefalos.vansales",
            ["X-App-Version"] = "1.9.7"
        };

        var result = await InvokeAsync(
            Evaluation(shouldForceUpgrade: true, currentVersion: "1.9.7"),
            headers,
            path: Path);

        var logged = Assert.Single(result.Logged);
        Assert.Contains("com.kefalos.vansales", logged, StringComparison.Ordinal);
        Assert.Contains("1.9.7", logged, StringComparison.Ordinal);
        Assert.Contains(Path, logged, StringComparison.Ordinal);
    }

    private static string AssertOneUnforgeableLine(IReadOnlyList<string> logged, string stillReadable)
    {
        var line = Assert.Single(logged);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Single(line.Split('\n'));

        // Still readable: sanitising replaces the control characters rather than dropping the value.
        Assert.Contains(stillReadable, line, StringComparison.Ordinal);
        Assert.Contains(Forged, line, StringComparison.Ordinal);

        return line;
    }

    /// <summary>
    /// The path needs an assertion of its own, because the line-break checks above cannot see
    /// whether it was the sanitiser that made it safe.
    /// </summary>
    /// <remarks>
    /// <c>HttpRequest.Path</c> is a <c>PathString</c> whose <c>ToString</c> is
    /// <c>ToUriComponent()</c>, so logging the struct renders a newline as <c>%0A</c> — safe, but
    /// as a side effect of the struct rather than of any guard, and unpinnable by a test. The
    /// middleware therefore sanitises <c>Path.Value</c>, and this asserts the result carries the
    /// sanitiser's mark rather than the escaping.
    /// </remarks>
    private static void AssertThePathWentThroughTheSanitiser(string logged)
    {
        // The sanitiser replaces each control character with '?'. If the guard were dropped, the
        // raw Path.Value would arrive with the line break intact (caught above); passing the
        // PathString instead would render "%0A" and escape the rest of the path with it.
        Assert.Contains(Path + "?", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("%0A", logged, StringComparison.OrdinalIgnoreCase);
    }

    private static MobileVersionPolicyEvaluation Evaluation(
        bool hasValidVersionMetadata = true,
        bool shouldForceUpgrade = false,
        bool requireHeaders = false,
        string? currentVersion = null)
        => new(
            Status: shouldForceUpgrade ? "block" : "ok",
            CurrentVersion: currentVersion,
            LatestVersion: "2.0.1",
            RecommendedVersion: "2.0.1",
            MinimumSupportedVersion: "2.0.0",
            DownloadUrl: "https://play.google.com/store/apps/details?id=com.kefalos.vansales",
            ReleaseNotes: null,
            Message: null,
            ShouldForceUpgrade: shouldForceUpgrade,
            CheckedAtUtc: new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
            PolicyApplies: true,
            HasValidVersionMetadata: hasValidVersionMetadata,
            RequireHeaders: requireHeaders);

    private sealed record Result(bool ReachedTheApi, int StatusCode, IReadOnlyList<string> Logged);

    private static async Task<Result> InvokeAsync(
        MobileVersionPolicyEvaluation evaluation,
        Dictionary<string, string> headers,
        string path = Path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        context.Response.Body = new MemoryStream();

        var reachedTheApi = false;
        var logger = new CapturingLogger<MobileVersionEnforcementMiddleware>();
        var middleware = new MobileVersionEnforcementMiddleware(
            _ =>
            {
                reachedTheApi = true;
                return Task.CompletedTask;
            },
            logger,
            new StubEvaluator(evaluation));

        await middleware.InvokeAsync(context);

        return new Result(
            reachedTheApi,
            context.Response.StatusCode,
            logger.Entries.Select(entry => entry.Message).ToList());
    }

    private sealed class StubEvaluator(MobileVersionPolicyEvaluation evaluation) : IMobileVersionPolicyEvaluator
    {
        public MobileVersionPolicyEvaluation Evaluate(string? appId, string? platform, string? currentVersion)
            => evaluation;
    }
}
