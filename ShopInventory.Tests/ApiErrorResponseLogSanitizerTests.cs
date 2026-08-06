using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Guards the identifier sanitizer that keeps caller-supplied values from forging log entries.
/// </summary>
/// <remarks>
/// The value these cover reaches the log from a route segment — /gl-accounts/{Code} and its like —
/// so a caller chooses it. A carriage return in it ends the log line, and whatever follows is read
/// back as a separate entry that the caller wrote: a plausible-looking "user X signed in" sitting
/// in the middle of real entries. Structured logging is not a defence, because the console and file
/// sinks render the placeholder into the message text and the break lands in the output intact.
/// </remarks>
public class ApiErrorResponseLogSanitizerTests
{
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Identifier_sanitizer_strips_the_line_breaks_that_forge_an_entry(string lineBreak)
    {
        var forged = $"510000{lineBreak}2026-08-05 12:00:00 INFO Balance approved by admin";

        var sanitized = ApiErrorResponse.SanitizeIdentifierForLog(forged);

        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        // The text survives — it is the break that makes it a separate entry, and a reader needs to
        // see what was actually sent.
        Assert.Contains("Balance approved by admin", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void Identifier_sanitizer_strips_the_separators_IsControl_misses(char separator)
    {
        // char.IsControl returns false for both of these while plenty of sinks still break a line
        // on them, so they have to be named explicitly rather than left to the control-char check.
        Assert.False(char.IsControl(separator));

        var sanitized = ApiErrorResponse.SanitizeIdentifierForLog($"510000{separator}forged");

        Assert.DoesNotContain(separator, sanitized);
    }

    [Theory]
    [InlineData('\0')]
    [InlineData('\b')]
    [InlineData('\t')]
    [InlineData('\u001b')]
    public void Identifier_sanitizer_strips_the_control_characters_that_rewrite_a_terminal(char control)
    {
        var sanitized = ApiErrorResponse.SanitizeIdentifierForLog($"51{control}0000");

        Assert.DoesNotContain(control, sanitized);
    }

    [Theory]
    [InlineData("510000")]
    [InlineData("_SYS00000000123")]
    [InlineData("1100-01-001")]
    public void Identifier_sanitizer_leaves_a_real_account_code_alone(string accountCode)
    {
        Assert.Equal(accountCode, ApiErrorResponse.SanitizeIdentifierForLog(accountCode));
    }

    [Fact]
    public void Identifier_sanitizer_caps_a_route_segment_with_no_length_limit_of_its_own()
    {
        var sanitized = ApiErrorResponse.SanitizeIdentifierForLog(new string('5', 10_000));

        Assert.EndsWith("... [truncated]", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length < 100, $"a capped identifier ran to {sanitized.Length} characters");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Identifier_sanitizer_names_an_absent_value_rather_than_leaving_a_gap(string? value)
    {
        // An empty string renders as nothing at all in the message, which reads like the logging
        // itself is broken rather than like the value was missing.
        Assert.Equal("(none)", ApiErrorResponse.SanitizeIdentifierForLog(value));
    }

    [Fact]
    public void Body_sanitizer_still_keeps_the_line_breaks_an_error_body_needs()
    {
        // The two sanitizers are deliberately different: a response body is expected to be
        // multi-line and is read as one block, so collapsing it would cost more than it protects.
        // This is here so nobody "fixes" the difference by pointing identifiers at this one.
        var sanitized = ApiErrorResponse.SanitizeForLog("first line\nsecond line");

        Assert.Contains('\n', sanitized);
    }

    [Fact]
    public void Body_sanitizer_still_redacts_a_token()
    {
        var sanitized = ApiErrorResponse.SanitizeForLog("Request failed: token=abc123def");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
    }

    [Fact(Skip = "Known gap in the Web body sanitizer, not introduced here — see the remarks.")]
    public void Body_sanitizer_does_not_yet_redact_a_token_in_a_JSON_body()
    {
        // Pinned rather than deleted, because this is the shape that actually arrives: the API
        // answers JSON, and this sanitizer's regex wants the key touching the colon, so the quote
        // in "token":"..." means it matches nothing and the value is logged whole. The API project
        // has a sanitizer that parses the JSON and redacts by key; this one never got it. Fixing it
        // changes what ~10 existing call sites write to the log, so it wants its own change rather
        // than a ride on a CodeQL fix.
        var sanitized = ApiErrorResponse.SanitizeForLog("{\"token\":\"abc123def\"}");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
    }
}
