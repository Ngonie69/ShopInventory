using System.Globalization;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Counts the <c>SQLQueries</c> objects the target company holds, grouped by the family their code
/// belongs to, and snapshots the full code list so a later run can say what was added.
/// </summary>
/// <remarks>
/// Strictly read-only: a <c>Login</c>, paged <c>GET SQLQueries?$select=SqlCode</c>, a <c>Logout</c>.
/// It provisions nothing, so it sits behind the plain SAP opt-in rather than the SQL one — the
/// question it answers is precisely "how much has the SQL opt-in cost us", and a probe that itself
/// left rows behind could not answer it.
///
/// Two samples are what make the answer mean anything. One run gives a number; a second run during
/// confirmed POD activity gives the growth rate, which is the open question. Snapshots are keyed by
/// company so a run against the wrong one cannot silently diff against the right one's history.
/// </remarks>
public sealed class SapSqlQueryCensusTests
{
    /// <summary>Where snapshots are written. Defaults under the temp directory.</summary>
    private const string SnapshotDirVariable = "SHOPINVENTORY_SAP_CENSUS_DIR";

    /// <summary>
    /// An existing <c>B1SESSION</c> to borrow instead of logging in.
    /// </summary>
    /// <remarks>
    /// The credentials in user secrets no longer authenticate against any company, so a session
    /// handed over from a browser or another tool is the only way to reach production from here.
    /// A borrowed session is never logged out — that would end it for whoever lent it.
    /// </remarks>
    private const string SessionVariable = "SHOPINVENTORY_SAP_SESSION";

    /// <summary>
    /// SAP answers 20 rows without this, which turns a 5,000-row census into 250 round trips.
    /// </summary>
    private const int PageSize = 500;

    /// <summary>
    /// The trailing fingerprint <c>BuildContentAddressedQueryCode</c> appends: 12 hex characters of
    /// SHA-256 over the statement. Stripping it leaves the family, which is what we want to count.
    /// </summary>
    private static readonly Regex ContentAddressedSuffix =
        new("^(?<family>.+)_(?<fingerprint>[0-9A-F]{12})$", RegexOptions.Compiled);

    /// <summary>
    /// The shapes that predate content-addressing — a code ending in a run of plain digits that is
    /// not a 12-hex fingerprint. These are the orphans: nothing in the current code can ever ask
    /// for them again, so they are pure dead weight.
    /// </summary>
    private static readonly Regex LegacyRandomSuffix =
        new(@"^(?<family>.+?)_?(?<suffix>\d{5,})$", RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public SapSqlQueryCensusTests(ITestOutputHelper output) => _output = output;

    [SapFact]
    public async Task Census_of_sql_query_objects()
    {
        var settings = SapAvailability.Settings;

        using var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(settings.ServiceLayerUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var borrowedSession = Environment.GetEnvironmentVariable(SessionVariable);
        var isBorrowed = !string.IsNullOrWhiteSpace(borrowedSession);

        _output.WriteLine($"Service Layer : {settings.ServiceLayerUrl}");
        _output.WriteLine(string.Empty);

        var sessionId = isBorrowed
            ? borrowedSession!.Trim()
            : await LoginAsync(http, settings.CompanyDB, settings.Username, settings.Password);

        // A borrowed session belongs to whatever company its owner logged into, which need not be
        // the one configured here. Ask SAP rather than assume: a census labelled with the wrong
        // company is worse than no census, because it would diff against the wrong history.
        var companyDb = isBorrowed
            ? await IdentifyCompanyAsync(http, sessionId) ?? "UNKNOWN"
            : settings.CompanyDB;

        _output.WriteLine($"Company       : {companyDb}{(isBorrowed ? " (from borrowed session)" : string.Empty)}");
        _output.WriteLine(string.Empty);

        List<string> codes;
        try
        {
            codes = await ReadAllSqlCodesAsync(http, sessionId);
        }
        finally
        {
            // Only end a session this test started.
            if (!isBorrowed)
            {
                await LogoutAsync(http, sessionId);
            }
        }

        Report(codes, companyDb);

        Assert.NotEmpty(codes);
    }

    private async Task<string> LoginAsync(HttpClient http, string companyDb, string userName, string password)
    {
        using var response = await http.PostAsJsonAsync(
            "Login",
            new { CompanyDB = companyDb, UserName = userName, Password = password });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Login to '{companyDb}' failed with {(int)response.StatusCode}: {Truncate(body, 400)}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("SessionId").GetString()
            ?? throw new InvalidOperationException("Login succeeded but returned no SessionId.");
    }

    /// <summary>
    /// Asks the Service Layer which company the session is attached to. Returns null when SAP will
    /// not say, so the caller can label the census honestly rather than guess.
    /// </summary>
    private async Task<string?> IdentifyCompanyAsync(HttpClient http, string sessionId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "CompanyService_GetCompanyInfo");
            request.Headers.Add("Cookie", $"B1SESSION={sessionId}");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"(could not identify company: {(int)response.StatusCode} {Truncate(body, 200)})");
                return null;
            }

            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "CompanyDB", "CompanyName", "DatabaseName" })
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }

            _output.WriteLine($"(company info returned no recognised field: {Truncate(body, 300)})");
            return null;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"(could not identify company: {ex.Message})");
            return null;
        }
    }

    private static async Task LogoutAsync(HttpClient http, string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Logout");
        request.Headers.Add("Cookie", $"B1SESSION={sessionId}");
        using var response = await http.SendAsync(request);
        _ = response;
    }

    /// <summary>
    /// Pages the whole entity set. Selects only <c>SqlCode</c> — pulling <c>SqlText</c> for
    /// thousands of rows would move megabytes to answer a counting question.
    /// </summary>
    private async Task<List<string>> ReadAllSqlCodesAsync(HttpClient http, string sessionId)
    {
        var codes = new List<string>();
        var skip = 0;

        while (true)
        {
            var url = skip == 0
                ? "SQLQueries?$select=SqlCode"
                : $"SQLQueries?$select=SqlCode&$skip={skip}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={sessionId}");
            request.Headers.Add("Prefer", $"odata.maxpagesize={PageSize}");

            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.IsSuccessStatusCode,
                $"GET {url} failed with {(int)response.StatusCode}: {Truncate(body, 400)}");

            using var document = JsonDocument.Parse(body);
            var page = document.RootElement.GetProperty("value");

            var pageCount = 0;
            foreach (var row in page.EnumerateArray())
            {
                if (row.TryGetProperty("SqlCode", out var sqlCode) && sqlCode.GetString() is { } code)
                {
                    codes.Add(code);
                }

                pageCount++;
            }

            if (pageCount == 0)
            {
                break;
            }

            skip += pageCount;
            _output.WriteLine($"  read {codes.Count} codes...");

            // No odata.nextLink means the set is exhausted; SAP omits it on the final page.
            if (!document.RootElement.TryGetProperty("odata.nextLink", out _)
                && !document.RootElement.TryGetProperty("@odata.nextLink", out _))
            {
                break;
            }
        }

        return codes;
    }

    private void Report(List<string> codes, string companyDb)
    {
        var families = codes
            .GroupBy(ClassifyFamily, StringComparer.Ordinal)
            .Select(group => (Family: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Family, StringComparer.Ordinal)
            .ToList();

        _output.WriteLine(string.Empty);
        _output.WriteLine($"TOTAL SQLQueries rows: {codes.Count}");
        _output.WriteLine($"Distinct families    : {families.Count}");
        _output.WriteLine(string.Empty);
        _output.WriteLine("Count  Family");
        _output.WriteLine("-----  ------------------------------------------");

        foreach (var (family, count) in families)
        {
            _output.WriteLine($"{count,5}  {family}");
        }

        var snapshotPath = WriteSnapshot(codes, companyDb);
        _output.WriteLine(string.Empty);
        _output.WriteLine($"Snapshot written: {snapshotPath}");

        DiffAgainstPreviousSnapshot(codes, companyDb, snapshotPath);
    }

    /// <summary>
    /// Reduces a code to the family that generated it, so the census counts generators rather than
    /// objects. A content-addressed code loses its fingerprint; a legacy code loses its random
    /// suffix and is tagged so the two are never summed together.
    /// </summary>
    private static string ClassifyFamily(string code)
    {
        var contentAddressed = ContentAddressedSuffix.Match(code);
        if (contentAddressed.Success)
        {
            return contentAddressed.Groups["family"].Value;
        }

        var legacy = LegacyRandomSuffix.Match(code);
        if (legacy.Success)
        {
            return $"{legacy.Groups["family"].Value} (legacy suffix)";
        }

        return $"{code} (fixed)";
    }

    private static string WriteSnapshot(List<string> codes, string companyDb)
    {
        var directory = Environment.GetEnvironmentVariable(SnapshotDirVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "shopinventory-sap-census");

        Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{Sanitize(companyDb)}-{stamp}.txt");

        File.WriteAllLines(path, codes.OrderBy(code => code, StringComparer.Ordinal));
        return path;
    }

    /// <summary>
    /// Compares this run against the most recent earlier snapshot of the same company. This is the
    /// half that answers the actual question: a count is a number, a diff is a growth rate.
    /// </summary>
    private void DiffAgainstPreviousSnapshot(List<string> codes, string companyDb, string currentPath)
    {
        var directory = Path.GetDirectoryName(currentPath)!;

        // Compare file names, not full paths. Path.Combine and Directory.EnumerateFiles disagree
        // about separators when the configured directory uses forward slashes, so a full-path
        // comparison failed to exclude the file just written and the census diffed against itself —
        // reporting a reassuring "added: 0" that meant nothing.
        var currentName = Path.GetFileName(currentPath);
        var previous = Directory
            .EnumerateFiles(directory, $"{Sanitize(companyDb)}-*.txt")
            .Where(path => !string.Equals(Path.GetFileName(path), currentName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (previous is null)
        {
            _output.WriteLine(string.Empty);
            _output.WriteLine(
                "No earlier snapshot for this company. Run this again after a busy period to get a "
                + "growth rate — a single sample cannot distinguish a fix from a quiet window.");
            return;
        }

        var before = new HashSet<string>(File.ReadAllLines(previous), StringComparer.Ordinal);
        var now = new HashSet<string>(codes, StringComparer.Ordinal);

        var added = now.Except(before, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var removed = before.Except(now, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        _output.WriteLine(string.Empty);
        _output.WriteLine($"Compared against: {Path.GetFileName(previous)}");
        _output.WriteLine($"  before : {before.Count}");
        _output.WriteLine($"  now    : {now.Count}");
        _output.WriteLine($"  added  : {added.Count}");
        _output.WriteLine($"  removed: {removed.Count}");

        foreach (var code in added.Take(100))
        {
            _output.WriteLine($"  + {code}");
        }

        if (added.Count > 100)
        {
            _output.WriteLine($"  ... and {added.Count - 100} more");
        }

        foreach (var code in removed.Take(20))
        {
            _output.WriteLine($"  - {code}");
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";
}
