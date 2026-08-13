using ShopInventory.Common.Security;

namespace ShopInventory.Tests;

/// <summary>
/// A request path reaches the log already percent-decoded, so <c>%0A</c> in the URL arrives here as
/// a real newline and would end the log line early — everything after it reads back as an entry the
/// caller wrote. Flagged by CodeQL on PR #249.
/// </summary>
public class SensitiveDataSanitizerIdentifierTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void Line_breaks_cannot_forge_a_second_entry(string separator)
    {
        var forged = $"/api/invoice/1{separator}2026-08-13 08:18:24.002 +02:00 [INF] Handled LoginCommand";

        var sanitized = SensitiveDataSanitizer.SanitizeIdentifierForLog(forged);

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
    }

    [Theory]
    [InlineData((char)0x2028)]
    [InlineData((char)0x2029)]
    [InlineData('\0')]
    [InlineData('\b')]
    public void Control_and_unicode_line_separators_are_replaced(char hostile)
    {
        var sanitized = SensitiveDataSanitizer.SanitizeIdentifierForLog($"/api/invoice{hostile}/pod");

        // char.IsControl is false for U+2028 and U+2029, which is why they are named explicitly in
        // the implementation rather than left to it.
        Assert.DoesNotContain(hostile, sanitized);
        Assert.Contains('?', sanitized);
    }

    [Fact]
    public void An_ordinary_request_path_is_left_alone()
    {
        const string path = "/api/invoice/2201332/crate-pod";

        Assert.Equal(path, SensitiveDataSanitizer.SanitizeIdentifierForLog(path));
    }

    [Fact]
    public void An_unbounded_path_is_truncated()
    {
        var sanitized = SensitiveDataSanitizer.SanitizeIdentifierForLog($"/api/{new string('x', 10_000)}");

        Assert.EndsWith("... [truncated]", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length < 100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_value_reads_as_absent_rather_than_as_a_gap(string? value)
    {
        Assert.Equal("(none)", SensitiveDataSanitizer.SanitizeIdentifierForLog(value));
    }

    [Fact]
    public void SanitizeForLog_is_still_the_wrong_tool_for_this()
    {
        // Guards the reason the new method exists: the body sanitizer deliberately keeps line
        // breaks, because a response body is expected to be multi-line and readable.
        var sanitized = SensitiveDataSanitizer.SanitizeForLog("first line\nsecond line");

        Assert.Contains('\n', sanitized);
    }
}
