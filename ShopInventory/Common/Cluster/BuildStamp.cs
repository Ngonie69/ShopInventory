using System.Globalization;
using System.Reflection;

namespace ShopInventory.Common.Cluster;

/// <summary>
/// When this build was published, read from the assembly metadata the deploy stamps into it.
/// </summary>
/// <remarks>
/// Set by <c>Update-Production.ps1</c> as <c>-p:BuildTimestampUtc=…</c> at publish time. A build made
/// any other way carries no stamp, and <see cref="TimestampUtc"/> is then null on purpose: a developer
/// build must not be ordered against a deployed one in either direction (see
/// <see cref="StaleBuildDecision"/>). The value is round-trip ("O") UTC.
/// </remarks>
public static class BuildStamp
{
    static BuildStamp()
    {
        TimestampUtc = Parse(typeof(BuildStamp).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "BuildTimestampUtc", StringComparison.Ordinal))
            ?.Value);
    }

    /// <summary>
    /// Reads the stamp the deploy wrote. PowerShell's round-trip format ("o") is what
    /// <c>Update-Production.ps1</c> produces, and anything unparseable is treated as no stamp at all —
    /// an unreadable stamp must not be guessed at, because the guess decides whether a node works.
    /// </summary>
    internal static DateTime? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // RoundtripKind alone: combining it with AdjustToUniversal throws, which would have taken down
        // every stamped build at startup.
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return null;
        }

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            // No zone in the string: the deploy writes UTC, so read it as UTC rather than as the
            // reader's local time, which would move the stamp by the machine's offset.
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        };
    }

    /// <summary>When the deploy published this build, or null when it was not published by one.</summary>
    public static DateTime? TimestampUtc { get; }

    /// <summary>Something loggable in both cases.</summary>
    public static string Description =>
        TimestampUtc is { } stamp ? stamp.ToString("yyyy-MM-dd HH:mm:ss'Z'") : "unstamped (not published by a deploy)";
}

/// <summary>
/// The build stamp this process reports to the cluster. A singleton so a test can state a build
/// without publishing one.
/// </summary>
public sealed record BuildStampProvider(DateTime? TimestampUtc)
{
    public static BuildStampProvider FromAssembly() => new(BuildStamp.TimestampUtc);

    public string Description =>
        TimestampUtc is { } stamp ? stamp.ToString("yyyy-MM-dd HH:mm:ss'Z'") : "unstamped (not published by a deploy)";
}
