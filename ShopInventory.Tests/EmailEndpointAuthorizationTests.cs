using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Controllers;

namespace ShopInventory.Tests;

/// <summary>
/// <see cref="EmailController"/> sends mail from the server's own SMTP identity, and
/// <c>POST /api/Email/test</c> takes the recipient from the request body. It carried
/// <c>[AllowAnonymous]</c> until 2026-08-17, so anyone who could reach the API could make this
/// server send mail to an address of their choosing, with only the global rate limiter in front of
/// it. Nothing in the repo ever called it that way — no page, no deployment script, no test — so it
/// was opened up for a reason nobody wrote down and nobody needed.
///
/// A single attribute is all that stands between the current behaviour and that one, and its absence
/// is invisible in review: the controller-level <c>[Authorize]</c> is thirty lines away and reads as
/// if it covers everything below it. Hence a test that asserts the absence.
/// </summary>
public sealed class EmailEndpointAuthorizationTests
{
    [Fact]
    public void The_test_email_endpoint_is_not_anonymous()
    {
        var action = typeof(EmailController).GetMethod(nameof(EmailController.SendTestEmail));

        Assert.NotNull(action);
        Assert.Empty(action!.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void No_action_on_the_email_controller_is_anonymous()
    {
        // Stated over the whole controller rather than the one action, because the next one added is
        // the one nobody thinks to write a test for.
        var anonymous = typeof(EmailController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(anonymous);
    }

    [Fact]
    public void The_controller_still_requires_the_ApiAccess_policy()
    {
        // The guard above only means anything while the class-level requirement is there to inherit.
        var authorize = typeof(EmailController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToArray();

        Assert.Single(authorize);
        Assert.Equal("ApiAccess", authorize[0].Policy);
    }
}
