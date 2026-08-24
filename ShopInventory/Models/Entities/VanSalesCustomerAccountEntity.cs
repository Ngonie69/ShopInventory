using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A van sales customer's own sign-in on the customer ordering app.
///
/// Deliberately not a <see cref="User"/>. A User is an employee: it carries a role from
/// <see cref="ApplicationRoles"/>, and every one of those roles is in
/// <c>ApplicationRoles.ApiAccessRoles</c>, which is what the "ApiAccess" policy guarding almost the
/// whole API requires. Giving a shopkeeper a User row to log in with would put them one missing
/// attribute away from the staff API. They are a separate subject with a separate table, a separate
/// token audience claim and a separate policy, so that the default for a customer on any staff
/// endpoint is refusal rather than permission.
/// </summary>
/// <remarks>
/// One account per phone number, not per customer: the phone is what the customer proves ownership
/// of, and it is the only credential in this flow. A shop with two phones gets two accounts pointed
/// at the same <see cref="RouteCustomerEntity"/>, which is the honest description of what is
/// happening and keeps revocation per-device rather than per-shop.
/// </remarks>
[Index(nameof(PhoneE164), IsUnique = true)]
[Index(nameof(RouteCustomerId))]
public class VanSalesCustomerAccountEntity
{
    [Key]
    public int Id { get; set; }

    public int RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    /// <summary>
    /// The customer's phone in E.164 (<c>+263771234567</c>), normalised on the way in.
    ///
    /// Stored in one canonical form because it is the lookup key for sign-in, and the same phone
    /// written four ways — <c>0771234567</c>, <c>263771234567</c>, <c>+263 77 123 4567</c> — is one
    /// customer to a person and four rows to a unique index.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string PhoneE164 { get; set; } = null!;

    /// <summary>Who the person is, for greeting them and for the operator's own list.</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this account may sign in. Deactivating is the revocation path — the row is kept so
    /// the orders it placed still resolve to a signer.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Consecutive failed OTP verifications. Reset on success; drives <see cref="LockedUntil"/>.
    /// </summary>
    public int FailedOtpCount { get; set; }

    /// <summary>
    /// Set when too many codes have been got wrong in a row. A lockout on the account rather than
    /// only a rate limit on the endpoint, because the endpoint's limiter partitions by IP and a
    /// whole town can share one mobile NAT.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Whether a lockout is currently in force.</summary>
    [NotMapped]
    public bool IsLockedOut => LockedUntil is { } until && until > DateTime.UtcNow;
}
