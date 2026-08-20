using FirebaseAdmin.Messaging;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers which FCM failures cost a device its registration.
/// </summary>
/// <remarks>
/// Production logged <c>FCM send failed for token cIqH8sOKRVCP...: null</c> — the messaging error
/// code was null, so the line said nothing, and the token was left registered because the pruning
/// only matched two codes. Both halves matter: a permanent failure should take the registration with
/// it, and everything else must not, because revoking a working handset silently stops it receiving
/// anything at all.
/// </remarks>
public sealed class PushTokenPruningTests
{
    [Theory]
    [InlineData(MessagingErrorCode.Unregistered)]        // app uninstalled, or the token rotated
    [InlineData(MessagingErrorCode.InvalidArgument)]     // malformed token
    [InlineData(MessagingErrorCode.SenderIdMismatch)]    // minted for a different Firebase project
    [InlineData(MessagingErrorCode.ThirdPartyAuthError)] // bound to an APNs credential we do not hold
    public void A_permanently_broken_token_is_revoked(MessagingErrorCode error)
    {
        Assert.True(IsPermanentTokenFailure(error));
    }

    [Theory]
    [InlineData(MessagingErrorCode.Unavailable)]
    [InlineData(MessagingErrorCode.QuotaExceeded)]
    [InlineData(MessagingErrorCode.Internal)]
    public void A_transient_failure_leaves_the_registration_alone(MessagingErrorCode error)
    {
        Assert.False(IsPermanentTokenFailure(error));
    }

    /// <summary>
    /// The 2026-08-20 case: FCM returned a failure it did not classify as a messaging fault, so the
    /// code was null. Unknown is not permanent — revoking on it would let one transport hiccup
    /// unregister a working handset.
    /// </summary>
    [Fact]
    public void An_unclassified_failure_is_not_treated_as_permanent()
    {
        Assert.False(IsPermanentTokenFailure(null));
    }

    private static bool IsPermanentTokenFailure(MessagingErrorCode? error)
        => PushNotificationService.IsPermanentTokenFailure(error);
}
