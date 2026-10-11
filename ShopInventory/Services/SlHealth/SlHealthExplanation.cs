namespace ShopInventory.Services.SlHealth;

/// <summary>
/// SL Health's answer as a few words a person can read: why SAP is unavailable, or what about it is
/// failing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ForStaff"/> is built only from what SL Health reports as structure: the planned window,
/// the restart, and which of its checks failed. It never repeats SL Health's own reason sentences or a
/// check's error text, because those are written for administrators and can name a host or a port, and
/// the staff banner is on every page of every user.
/// </para>
/// <para>
/// <see cref="ForRecord"/> is for the outage row and the health check, which administrators read, and
/// falls back to those reason sentences when the structure says nothing.
/// </para>
/// </remarks>
public static class SlHealthExplanation
{
    /// <summary>The longest text kept on an outage row.</summary>
    public const int MaxRecordLength = 500;

    private const int MaxReasonLength = 120;

    /// <summary>
    /// For example <c>planned maintenance until 03:00 (HANA backup)</c>, <c>SAP is being restarted</c> or
    /// <c>login is failing: Server Tools not answering</c>. Null when SL Health has nothing to add.
    /// </summary>
    public static string? ForStaff(SlHealthStatus? status, DateTime nowUtc)
    {
        if (status is null)
        {
            return null;
        }

        var parts = new List<string>(3);

        if (status.Restart is { } restart)
        {
            parts.Add(restart.IsServiceLayer ? "SAP is being restarted" : "SAP's Server Tools are being restarted");
        }

        if (status.Maintenance is { } window && FromUnixMs(window.Until) is { } until && until > nowUtc)
        {
            var reason = Clean(window.Reason, MaxReasonLength);
            parts.Add($"planned maintenance until {Clock(until, nowUtc)}" + (reason is null ? string.Empty : $" ({reason})"));
        }

        if (FailingChecks(status) is { } failing)
        {
            parts.Add(failing);
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// <see cref="ForStaff"/>, or failing that SL Health's level and its own reasons while it reports the
    /// Service Layer at EXTREME LOAD or DOWN. Null when it reports nothing wrong.
    /// </summary>
    public static string? ForRecord(SlHealthStatus? status, DateTime nowUtc)
    {
        if (status is null)
        {
            return null;
        }

        var text = ForStaff(status, nowUtc);

        if (text is null && status.AdviceValue is SlHealthAdvice.Unavailable or SlHealthAdvice.HoldBackground)
        {
            var reasons = string.Join("; ", (status.Reasons ?? []).Select(reason => reason?.Trim()).Where(reason => !string.IsNullOrEmpty(reason)));
            text = string.IsNullOrEmpty(reasons) ? status.Label ?? status.Level : $"{status.Label ?? status.Level}: {reasons}";
        }

        return Clean(text, MaxRecordLength);
    }

    /// <summary>Which of SL Health's own checks against SAP are failing, or null when none is.</summary>
    private static string? FailingChecks(SlHealthStatus status)
    {
        var parts = new List<string>(2);
        var serverToolsDown = status.ServerTools is { Ok: false };

        if (status.Login is { Ok: false })
        {
            parts.Add(serverToolsDown ? "login is failing: Server Tools not answering" : "login is failing");
        }
        else if (serverToolsDown)
        {
            // Sessions already open keep working, which is why a ping still answers.
            parts.Add("new logins will fail: Server Tools not answering");
        }

        var queries = (status.Queries ?? [])
            .Where(query => query is { Ok: false })
            .Select(query => Clean(query.Name, 40))
            .ToList();

        if (queries.Count == 1)
        {
            parts.Add(queries[0] is { } name ? $"the {name} query is failing" : "a test query is failing");
        }
        else if (queries.Count > 1)
        {
            parts.Add($"{queries.Count} test queries are failing");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The time by the clock in CAT when it is today or within half a day, otherwise the day as well.</summary>
    private static string Clock(DateTime utc, DateTime nowUtc)
    {
        var cat = AuditService.ToCAT(utc);
        return cat.Date == AuditService.ToCAT(nowUtc).Date || utc - nowUtc <= TimeSpan.FromHours(12)
            ? cat.ToString("HH:mm")
            : cat.ToString("ddd d MMM HH:mm");
    }

    private static DateTime? FromUnixMs(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>One line of printable text, no longer than asked. Whatever SL Health sent, a person typed some of it.</summary>
    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = string.Join(' ', new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }
}
