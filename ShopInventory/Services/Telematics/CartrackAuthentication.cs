using System.Net.Http.Headers;
using System.Text;
using ShopInventory.Configuration;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// Builds the Basic credential Cartrack authenticate with.
/// </summary>
/// <remarks>
/// <para>
/// One line of real decision, in a class of its own so a test can reach it — the header itself is
/// attached in <c>Program.cs</c>'s client factory callback, which nothing can call.
/// </para>
/// <para>
/// The decision is the encoding. RFC 7617 leaves the charset to the server and defaults to
/// unspecified, and this is the only Basic-auth client in the codebase, so there is no house
/// precedent to follow. UTF-8 is chosen because it is what Fleetweb's own console posts, and
/// because the failure mode of guessing wrong is a bare <c>401</c> — identical to a wrong
/// password, on a request whose Authorization header is never logged. There would be nothing to
/// look at.
/// </para>
/// </remarks>
public static class CartrackAuthentication
{
    /// <summary>
    /// The <c>Authorization</c> header value, or null when either half of the credential is
    /// missing. Null rather than an empty Basic header: an empty credential is a 401 that reads
    /// like a rejected password instead of like configuration that was never supplied.
    /// </summary>
    public static AuthenticationHeaderValue? HeaderFor(CartrackSettings settings)
    {
        if (!settings.HasCredentials)
        {
            return null;
        }

        // The username is trimmed because it is typed into a field and copied out of a console;
        // the password never is, because trailing whitespace can be part of a generated secret.
        var credential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.Username.Trim()}:{settings.Password}"));

        return new AuthenticationHeaderValue("Basic", credential);
    }
}
