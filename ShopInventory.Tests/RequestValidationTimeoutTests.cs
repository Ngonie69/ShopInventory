using System.Text.RegularExpressions;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the request-validation middleware does when one of its heuristic patterns runs out of
/// its match budget: it logs and allows, it does not reject.
/// </summary>
/// <remarks>
/// It used to fail closed — a RegexMatchTimeoutException was reported as threat "RegexTimeout" and the
/// request was answered with a 400. A timeout is not evidence of an attack, and the budget is easy to
/// exhaust for reasons that have nothing to do with the caller: RegexOptions.Compiled defers IL emit
/// and JIT to the first IsMatch, and that work runs inside that call's own timeout window. Measured on
/// an idle machine, the first match on the SQL pattern costs ~93 ms of a 200 ms budget while every
/// later match costs ~0.004 ms, so a loaded host only had to add ~107 ms of scheduling delay to turn a
/// benign request into a rejected one. RequestValidationSqlPatternTests failed exactly that way when
/// other test processes were running.
///
/// Two changes answer that. The patterns are warmed at type-initialization time so the compile cost is
/// paid while the pipeline is being built rather than inside a request — that part is a performance
/// measure and is not asserted here, because any assertion on it would be a timing race. And a timeout
/// now fails open, which is what these tests pin: the heuristics sit in front of parameterized queries,
/// so a wrong "attack" guess costs a customer their request while a wrong "unknown" guess falls through
/// to the real controls. Blocking would not have bought ReDoS protection either — the CPU is already
/// spent by the time the exception is raised, and the budget itself is what caps that.
/// </remarks>
public class RequestValidationTimeoutTests
{
    // Catastrophic backtracking against a one-tick budget: verified to throw on every one of 200 runs,
    // so these tests do not race the clock.
    private static readonly Regex AlwaysTimesOut =
        new(@"^(a|aa)+$", RegexOptions.None, TimeSpan.FromTicks(1));

    private static readonly string TimeoutBait = new string('a', 40) + "!";

    [Fact]
    public void APatternThatRunsOutOfItsBudgetIsNotTreatedAsAnAttack()
    {
        var patterns = new[] { new RequestValidationMiddleware.ThreatPattern(AlwaysTimesOut, "Poison") };

        var malicious = RequestValidationMiddleware.IsMalicious(
            TimeoutBait, out var threat, out var scanIncomplete, patterns);

        Assert.False(malicious);
        Assert.Equal(string.Empty, threat);
        Assert.True(scanIncomplete, "the caller has to be told the scan did not finish, so it can log it");
    }

    [Fact]
    public void APatternThatRunsOutOfItsBudgetDoesNotBlindThePatternsAfterIt()
    {
        // A payload that stalls the first pattern must not thereby skip the checks behind it.
        var sqlInjection = new Regex(@"union\s+select", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var patterns = new[]
        {
            new RequestValidationMiddleware.ThreatPattern(AlwaysTimesOut, "Poison"),
            new RequestValidationMiddleware.ThreatPattern(sqlInjection, "SQLInjection"),
        };

        var malicious = RequestValidationMiddleware.IsMalicious(
            TimeoutBait + " union select password from users", out var threat, out var scanIncomplete, patterns);

        Assert.True(malicious);
        Assert.Equal("SQLInjection", threat);
        Assert.True(scanIncomplete);
    }

    [Fact]
    public void AScanThatFinishesReportsNothingIncomplete()
    {
        var malicious = RequestValidationMiddleware.IsMalicious(
            "?id=F9--6N_Qk3Nu71I4wABSTA", out var threat, out var scanIncomplete);

        Assert.False(malicious, threat);
        Assert.False(scanIncomplete);
    }

    [Fact]
    public void RegexTimeoutIsNoLongerAThreatName()
    {
        var patterns = new[] { new RequestValidationMiddleware.ThreatPattern(AlwaysTimesOut, "Poison") };

        RequestValidationMiddleware.IsMalicious(TimeoutBait, out var threat, out _, patterns);

        // The name is gone from the vocabulary: nothing downstream should be able to log or alert on a
        // rejection that was really just a slow match.
        Assert.NotEqual("RegexTimeout", threat);
    }

    [Theory]
    // The production patterns still catch real payloads, and still let opaque tokens through, when the
    // scan completes normally. RequestValidationSqlPatternTests covers the SQL heuristic in depth;
    // these guard against the fail-open change quietly disarming the scanner altogether, and pin the
    // literal separators the traversal and redirect patterns depend on — a lost backslash there is
    // invisible to the compiler because the pattern still parses, just against the wrong characters.
    [InlineData("?q=x' UNION SELECT password FROM users--", true)]
    [InlineData("/api/files/../../etc/passwd", true)]
    [InlineData("/api/files/..\\..\\windows\\win.ini", true)]
    [InlineData("/api/files/..%2f..%2fsecrets", true)]
    [InlineData("?id=F9--6N_Qk3Nu71I4wABSTA", false)]
    [InlineData("/api/reports/profit-and-loss", false)]
    public void FailingOpenOnTimeoutDoesNotDisarmTheNormalPath(string query, bool expected)
    {
        Assert.Equal(expected, RequestValidationMiddleware.IsMalicious(query, out _, out _));
    }
}
