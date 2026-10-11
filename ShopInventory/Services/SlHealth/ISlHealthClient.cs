namespace ShopInventory.Services.SlHealth;

/// <summary>Asks SL Health what state the SAP Service Layer is in.</summary>
public interface ISlHealthClient
{
    /// <summary>
    /// One question, answered within <see cref="SlHealthClient.Timeout"/>. Never throws for anything SL
    /// Health did or failed to do: an answer that could not be had comes back as
    /// <see cref="SlHealthAnswer.Failure"/>.
    /// </summary>
    Task<SlHealthAnswer> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <param name="Status">What SL Health said, or null when it could not be asked or understood.</param>
/// <param name="Failure">Why there is no status, in a few words fit for a log line. Never the token.</param>
public sealed record SlHealthAnswer(SlHealthStatus? Status, string? Failure)
{
    public static SlHealthAnswer Of(SlHealthStatus status) => new(status, null);

    public static SlHealthAnswer Failed(string why) => new(null, why);
}
