namespace ShopInventory.Features.Maintenance;

/// <summary>
/// The mobile maintenance lockout: what it currently says, and how to change it.
/// </summary>
/// <remarks>
/// <para>
/// Stored in <c>SystemConfigs</c> rather than configuration for the reason the rate limits and the
/// van sales trading rules are: this is a decision taken while something is happening, by whoever
/// is doing the maintenance, and it has to take effect now. A setting that needs a web.config edit
/// and an app pool recycle — which is how the mobile <em>version</em> policy works — is no use for
/// a switch whose whole job is to be thrown five minutes before a migration and thrown back
/// afterwards. It is also why this is not an <c>appsettings</c> flag: the API runs on more than
/// one node, and they must all see the switch without a deploy.
/// </para>
/// <para>
/// <see cref="Current"/> sits on the path of every request, so it never blocks and never throws. It
/// answers from a snapshot reloaded at most once every
/// <see cref="MaintenanceStore.RefreshInterval"/>; a request that finds the snapshot stale
/// starts a reload in the background and is served the previous answer. The cost is that turning
/// the lockout on takes up to that interval to reach every node — acceptable, because the operator
/// throwing the switch is not posting invoices in the same second, and the alternative is a
/// database round trip per request on a system whose database is about to go down for maintenance.
/// </para>
/// </remarks>
public interface IMaintenanceStore
{
    /// <summary>
    /// The lockout as this instance understands it. Never null, never blocks, never throws.
    /// </summary>
    /// <remarks>
    /// This is the stored state, not the applied one: ask
    /// <see cref="MaintenanceState.IsActiveAt"/> whether it is actually in force, because a
    /// lockout with an end time that has passed is still <c>Enabled</c> and no longer applies.
    /// </remarks>
    MaintenanceState Current { get; }

    /// <summary>
    /// Persist a new lockout state and apply it on this instance at once.
    /// </summary>
    Task UpdateAsync(MaintenanceState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the stored state now rather than leaving it to the next stale read.
    /// </summary>
    /// <remarks>
    /// Called at startup so that a node joining mid-maintenance — an app pool recycle during the
    /// window, or the deploy that the maintenance is for — comes up already locked out rather than
    /// accepting transactions for its first few seconds.
    /// </remarks>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
