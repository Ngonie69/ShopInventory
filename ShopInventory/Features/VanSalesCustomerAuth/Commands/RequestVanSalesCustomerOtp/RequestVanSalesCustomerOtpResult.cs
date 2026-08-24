namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;

/// <summary>
/// What the app is told after asking for a code — which is the same thing every time.
/// </summary>
/// <remarks>
/// Both values come straight from configuration rather than from anything that happened, and that
/// is the point. A response that carried the real state — whether a message was sent, how long
/// until this particular number may ask again — would answer "does this shop have an account?" to
/// anyone who cared to ask, and the phone numbers of a supplier's customers are worth money to a
/// competitor. The app runs its countdown from <see cref="RetryAfterSeconds"/>; nothing else needs
/// to know.
/// </remarks>
public sealed record RequestVanSalesCustomerOtpResult(
    int RetryAfterSeconds,
    int ExpiresInSeconds);
