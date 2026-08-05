using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Guards the two log sanitizers on <see cref="ApiErrorResponse"/>: the identifier one that keeps
/// caller-supplied values from forging log entries, and the body one that keeps secrets out.
/// </summary>
/// <remarks>
/// <para>
/// The value the identifier tests cover reaches the log from a route segment — /gl-accounts/{Code}
/// and its like — so a caller chooses it. A carriage return in it ends the log line, and whatever
/// follows is read back as a separate entry that the caller wrote: a plausible-looking "user X
/// signed in" sitting in the middle of real entries. Structured logging is not a defence, because
/// the console and file sinks render the placeholder into the message text and the break lands in
/// the output intact.
/// </para>
/// <para>
/// The body tests cover the other direction — a response body on its way to a log. AuthService logs
/// the bodies of failed authentication calls, which are the ones most likely to carry a token, so
/// the JSON cases here are the shape that actually arrives rather than a hypothetical.
/// </para>
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

    [Fact]
    public void Body_sanitizer_redacts_a_token_in_a_JSON_body()
    {
        // The shape that actually arrives: the API answers JSON. The plain-text regex wants the key
        // touching the colon, so the quote in "token":"..." defeated it and the value was logged
        // whole — which is why the body path parses the JSON and redacts by key name instead.
        var sanitized = ApiErrorResponse.SanitizeForLog("{\"token\":\"abc123def\"}");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_reaches_a_token_nested_in_a_JSON_object()
    {
        // A regex over the raw text could be made to match a quoted key, but not to know which
        // object it sits in. Parsing is what gets us the depth.
        var sanitized = ApiErrorResponse.SanitizeForLog(
            "{\"error\":{\"detail\":\"sign-in failed\",\"data\":{\"accessToken\":\"abc123def\"}}}");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
        // The parts a reader needs are still there — redaction should cost the secret, not the body.
        Assert.Contains("sign-in failed", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_reaches_a_token_inside_a_JSON_array()
    {
        var sanitized = ApiErrorResponse.SanitizeForLog(
            "[{\"user\":\"ngoni\"},{\"session\":\"abc123def\"}]");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
        Assert.Contains("ngoni", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_redacts_a_whole_sensitive_subtree()
    {
        // A sensitive key whose value is an object or an array: redacting the key's own value only
        // if it happens to be a string would walk straight past this.
        var sanitized = ApiErrorResponse.SanitizeForLog(
            "{\"authorization\":{\"scheme\":\"Bearer\",\"parameter\":\"abc123def\"}}");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_still_uses_the_plain_text_path_for_a_non_JSON_body()
    {
        // Not every body is JSON — a bare string, an HTML error page, a transport message. The regex
        // is still the only thing covering those, so it has to stay reachable.
        var sanitized = ApiErrorResponse.SanitizeForLog(
            "Server returned 401: password=hunter2 for user ngoni");

        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.Contains("for user ngoni", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_falls_back_to_the_regex_when_a_JSON_body_does_not_parse()
    {
        // Opens like JSON but is truncated, which is how a body arrives when a response is cut off.
        // Dropping it would lose the log line; returning it as-is would leak the token.
        var sanitized = ApiErrorResponse.SanitizeForLog("{\"detail\":\"failed\", token=abc123def");

        Assert.DoesNotContain("abc123def", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_sanitizer_keeps_a_JSON_body_that_holds_no_secret_readable()
    {
        var sanitized = ApiErrorResponse.SanitizeForLog(
            "{\"detail\":\"The account code 510000 was not found.\"}");

        Assert.Contains("The account code 510000 was not found.", sanitized, StringComparison.Ordinal);
    }
}
