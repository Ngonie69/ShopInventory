using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.Password;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the gate that holds an account owing a password change to the password endpoints.
/// </summary>
/// <remarks>
/// It is the only enforcement the tills and handsets get, so both directions carry weight: a
/// request it should stop and lets through is a policy nobody is held to, and a request it should
/// allow and stops is an operator locked out of the one screen that would let them back in.
/// </remarks>
public sealed class PasswordChangeGateMiddlewareTests
{
    [Fact]
    public async Task With_enforcement_off_a_marked_account_is_not_stopped()
    {
        var outcome = await SendAsync("/api/invoice", marked: PasswordStanding.Expired, enforce: false);

        Assert.True(outcome.ReachedEndpoint);
    }

    [Fact]
    public async Task An_account_that_owes_nothing_is_not_stopped()
    {
        var outcome = await SendAsync("/api/invoice", marked: null, enforce: true);

        Assert.True(outcome.ReachedEndpoint);
    }

    [Theory]
    [InlineData("/api/invoice")]
    [InlineData("/api/DesktopIntegration/sales")]
    [InlineData("/api/vansales/order")]
    [InlineData("/api/user")]
    // Near misses on the allowed routes.
    [InlineData("/api/auth/register")]
    [InlineData("/api/usermanagement")]
    [InlineData("/api/passwords")]
    public async Task A_marked_account_is_refused_everything_but_the_password_routes(string path)
    {
        var outcome = await SendAsync(path, marked: PasswordStanding.Expired, enforce: true);

        Assert.False(outcome.ReachedEndpoint);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.Status);
    }

    [Theory]
    // The web portal, the till and the POD app.
    [InlineData("/api/password/change")]
    [InlineData("/api/Password/change")]
    [InlineData("/api/password/credentials")]
    // The merchandiser app.
    [InlineData("/api/Auth/change-password")]
    // The van app.
    [InlineData("/api/vansales/auth/password")]
    // What an app needs to draw a profile screen, and to leave.
    [InlineData("/api/auth/me")]
    [InlineData("/api/usermanagement/me")]
    [InlineData("/api/usermanagement/me/permissions")]
    [InlineData("/api/auth/logout")]
    public async Task A_marked_account_can_still_reach_every_clients_change_password_route(string path)
    {
        var outcome = await SendAsync(path, marked: PasswordStanding.Expired, enforce: true);

        Assert.True(outcome.ReachedEndpoint);
    }

    [Fact]
    public async Task An_anonymous_endpoint_is_never_gated_even_when_a_stale_token_rides_along()
    {
        // Sign-in and refresh are anonymous, and a client often sends its old token with them.
        var outcome = await SendAsync("/api/auth/login", marked: PasswordStanding.Expired, enforce: true, anonymousEndpoint: true);

        Assert.True(outcome.ReachedEndpoint);
    }

    [Fact]
    public async Task A_request_that_matches_no_endpoint_is_left_to_be_a_404()
    {
        var outcome = await SendAsync("/api/nothing-here", marked: PasswordStanding.Expired, enforce: true, hasEndpoint: false);

        Assert.True(outcome.ReachedEndpoint);
    }

    [Theory]
    [InlineData(PasswordStanding.Expired, "three months old")]
    [InlineData(PasswordStanding.Required, "has to be changed")]
    public async Task The_refusal_carries_its_sentence_under_every_name_a_client_reads(string reason, string says)
    {
        var outcome = await SendAsync("/api/invoice", marked: reason, enforce: true);

        Assert.StartsWith("application/problem+json", outcome.ContentType);

        using var body = JsonDocument.Parse(outcome.Body);
        var root = body.RootElement;

        // Problem-details readers take detail or title; the flat readers take message.
        foreach (var name in new[] { "detail", "title", "message" })
        {
            Assert.Contains(says, root.GetProperty(name).GetString());
        }

        Assert.Equal(PasswordChangeGateMiddleware.ErrorCode, root.GetProperty("code").GetString());
        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.True(root.GetProperty("passwordChangeRequired").GetBoolean());
        Assert.Equal(reason, root.GetProperty("passwordChangeReason").GetString());
    }

    private static async Task<Outcome> SendAsync(
        string path,
        string? marked,
        bool enforce,
        bool anonymousEndpoint = false,
        bool hasEndpoint = true)
    {
        var reached = false;
        var middleware = new PasswordChangeGateMiddleware(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            new FixedSettings(new PasswordPolicySettings { EnforceOnApiClients = enforce }),
            NullLogger<PasswordChangeGateMiddleware>.Instance);

        var claims = new List<Claim> { new(ClaimTypes.Name, "wellington.moyo") };
        if (marked is not null)
        {
            claims.Add(new Claim(PasswordStanding.ClaimType, marked));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (hasEndpoint)
        {
            var metadata = anonymousEndpoint
                ? new EndpointMetadataCollection(new AllowAnonymousAttribute())
                : EndpointMetadataCollection.Empty;
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        }

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return new Outcome(reached, context.Response.StatusCode, context.Response.ContentType ?? string.Empty, body);
    }

    private sealed record Outcome(bool ReachedEndpoint, int Status, string ContentType, string Body);

    private sealed class FixedSettings(PasswordPolicySettings value) : IOptionsMonitor<PasswordPolicySettings>
    {
        public PasswordPolicySettings CurrentValue => value;

        public PasswordPolicySettings Get(string? name) => value;

        public IDisposable? OnChange(Action<PasswordPolicySettings, string?> listener) => null;
    }
}
