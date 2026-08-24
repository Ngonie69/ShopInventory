using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A van sales customer's handset, for pushing order updates to.
/// </summary>
/// <remarks>
/// A table of its own rather than a nullable subject on <see cref="PushDeviceRegistration"/>, and
/// the reason is <c>PushNotificationService.SendToAllAsync</c>: it takes every non-revoked row in
/// that table with no filter on who owns it. A customer registration living there would receive
/// staff broadcasts — "deployment tonight", "SAP is down" — on a shopkeeper's phone. Separating the
/// tables makes that impossible rather than dependent on every present and future query remembering
/// to exclude customers.
/// <para>
/// What is shared is the part worth sharing: the FCM transport itself, through
/// <c>IPushNotificationService.SendToDeviceTokensAsync</c>. The Firebase setup, the batching and the
/// dead-token handling are written once.
/// </para>
/// </remarks>
[Index(nameof(DeviceToken), IsUnique = true)]
[Index(nameof(VanSalesCustomerAccountId))]
public class VanSalesCustomerDeviceEntity
{
    [Key]
    public int Id { get; set; }

    public int VanSalesCustomerAccountId { get; set; }

    [ForeignKey(nameof(VanSalesCustomerAccountId))]
    public VanSalesCustomerAccountEntity? Account { get; set; }

    /// <summary>The FCM registration token the handset reports at sign-in.</summary>
    [Required]
    [MaxLength(512)]
    public string DeviceToken { get; set; } = null!;

    /// <summary>
    /// The installation this token belongs to, matching the id used for session revocation.
    /// </summary>
    /// <remarks>
    /// Kept so that signing a device out can take its push registration with it. Otherwise a handset
    /// that was signed out — or lost — keeps receiving a shop's order updates.
    /// </remarks>
    [MaxLength(128)]
    public string? DeviceId { get; set; }

    [MaxLength(200)]
    public string? DeviceName { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastActiveAt { get; set; }

    /// <summary>Set when Firebase reports the token as dead, or the customer signs out.</summary>
    public bool IsRevoked { get; set; }
}
