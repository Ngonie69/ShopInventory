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
/// One account per phone number, not per customer: the phone is what the customer is known by, and
/// it is half of what they sign in with — the password on this row is the other half. A shop with
/// two phones gets two accounts pointed at the same <see cref="RouteCustomerEntity"/>, which is the
/// honest description of what is happening and keeps revocation per-device rather than per-shop.
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
    /// BCrypt hash of the password the shop signs in with, or null for an account that has never
    /// been given one.
    /// </summary>
    /// <remarks>
    /// Nullable because it has to be: every account created before passwords existed has none, and
    /// they keep working through the one-time code until an operator sets one. A null is not an open
    /// door — sign-in refuses it, and refuses it with the same sentence a wrong password gets, so it
    /// cannot be used to sort numbers into those that have a password and those that do not.
    /// <para>
    /// 255 rather than 60: BCrypt's output is 60 characters today, and a column sized exactly to
    /// today's algorithm is one that has to be migrated before the algorithm can ever change.
    /// </para>
    /// </remarks>
    [MaxLength(255)]
    public string? PasswordHash { get; set; }

    /// <summary>When the password was last set. For the operator's list and for support calls.</summary>
    public DateTime? PasswordSetAt { get; set; }

    /// <summary>
    /// Whether this account may sign in. Deactivating is the revocation path — the row is kept so
    /// the orders it placed still resolve to a signer.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Consecutive failed sign-in attempts, by code or by password. Reset on success; drives
    /// <see cref="LockedUntil"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately one counter for both credentials rather than one each. An attacker choosing
    /// whichever endpoint has attempts left would otherwise get two budgets against the same
    /// account, and the lockout exists to cap what an account can be subjected to in total. The
    /// column keeps its original name because renaming it buys a migration and nothing else.
    /// </remarks>
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
