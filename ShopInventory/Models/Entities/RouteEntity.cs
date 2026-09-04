using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A selling route — the round a van runs, the territory it belongs to, and the truck that runs it.
///
/// This exists because the departure compliance sheet is headed by three facts the system could not
/// state: "Territory: UPC", "Route: Guruve", "Truck Reg No: AHF0218". None of them were data anywhere.
/// A van's account carried an <c>AssignedSection</c>, but that is a depot name (Cheeseman, Graniteside)
/// and not a territory, and nothing at all named the route or the vehicle.
///
/// They live on one row rather than as three text fields on the user account for the reason any report
/// that groups by them needs: free text fragments. "Guruve", "guruve" and "Guruve " are one route to a
/// reader and three to a GROUP BY, and a compliance report that splits a route into three is worse than
/// no report. A van points at a route; the route says these things once.
/// </summary>
[Index(nameof(Code), IsUnique = true)]
[Index(nameof(SeedKey), IsUnique = true)]
public class RouteEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>Short stable identifier — what a report groups on, and what survives a rename.</summary>
    [Required]
    [MaxLength(30)]
    public string Code { get; set; } = null!;

    /// <summary>The route as people say it: "Guruve".</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    /// <summary>The territory the route sits in: "UPC".</summary>
    [MaxLength(100)]
    public string? Territory { get; set; }

    /// <summary>
    /// The vehicle normally assigned. A default rather than a fact about any particular day — a truck
    /// goes into the workshop and a route still runs — so the day's own record snapshots what was
    /// actually driven and this only supplies the value the rep confirms.
    /// </summary>
    [MaxLength(30)]
    public string? TruckRegNo { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Which route of the published schedule this is, or null on one somebody created themselves.
    /// </summary>
    /// <remarks>
    /// Written once, at insert, and never by an edit — see <see cref="RouteStopEntity.SeedKey"/>,
    /// which exists for the same reason and explains it at length. Matching on <see cref="Code"/>
    /// instead would work until the day somebody corrects a code, at which point the next start
    /// would decide the route was missing and create it a second time.
    /// </remarks>
    [MaxLength(60)]
    public string? SeedKey { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
