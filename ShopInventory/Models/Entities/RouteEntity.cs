using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
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
[Index(nameof(Code), IsUnique = true)] // live routes only — filtered in ApplicationDbContext
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

    /// <summary>
    /// The temperature the load on this round has to stay between, in Celsius, or null on a round
    /// that carries nothing chilled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These sit on the route rather than on the vehicle because they describe the load, not the
    /// box: a frozen round and an ambient round are held to different figures whichever truck
    /// happens to be running them that week. The consequence, stated so it is not discovered
    /// later: a route running a substitute truck is judged against the usual round's limits. The
    /// day's rollup snapshots whatever was set at the time it was built, so widening a limit
    /// today cannot quietly erase last month's breach.
    /// </para>
    /// <para>
    /// Both null means this round is never flagged on temperature — which is the right default,
    /// because a plain van with no probe fitted would otherwise report a breach every day by
    /// having no reading at all.
    /// </para>
    /// </remarks>
    [Column(TypeName = "decimal(4,1)")]
    public decimal? TemperatureMinC { get; set; }

    /// <inheritdoc cref="TemperatureMinC"/>
    [Column(TypeName = "decimal(4,1)")]
    public decimal? TemperatureMaxC { get; set; }

    /// <summary>
    /// Which of the tracker's four temperature probes is the load box, or null to use the
    /// lowest-numbered probe that reported.
    /// </summary>
    /// <remarks>
    /// The fleet API publishes four temperature channels per vehicle and no flag saying which are
    /// fitted or what any of them is measuring — a reading from probe 2 could be the box, the cab
    /// or nothing at all. So this cannot be discovered and has to be told, once, by somebody who
    /// looked.
    /// </remarks>
    public byte? TemperatureProbeChannel { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When the route was deleted from the routes page, or null on a live one.
    /// </summary>
    /// <remarks>
    /// A delete keeps the row. Trading days that already happened point at it, and a seeded route
    /// whose row disappeared would be put straight back by the seeder on the next start — it matches
    /// on <see cref="SeedKey"/>. A deleted route is left out of every list and refuses every write,
    /// and its code is free for a new route: the unique index on <see cref="Code"/> covers live
    /// routes only.
    /// </remarks>
    public DateTime? DeletedAt { get; set; }

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
