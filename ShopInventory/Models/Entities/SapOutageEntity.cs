using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One stretch of time in which SAP could not be used, as the whole cluster saw it.
/// </summary>
/// <remarks>
/// <para>
/// Written only by <c>SapAvailabilityProbeJob</c>, which runs on one node at a time, so there is at
/// most one open row (<see cref="EndedAtUtc"/> null) and nothing else races it.
/// </para>
/// <para>
/// The per-node circuit breaker answers "may this node send a request right now"; this answers "was SAP
/// down, and from when to when". The second is what the posting passes need after an outage: a sale
/// captured on the first day of a four-day outage is older than a three-day lookback by the time SAP
/// returns, and without a record of the outage there is nothing to say it is owed rather than abandoned.
/// </para>
/// </remarks>
[Table("SapOutages")]
[Index(nameof(EndedAtUtc))]
[Index(nameof(StartedAtUtc))]
public class SapOutageEntity
{
    public int Id { get; set; }

    /// <summary>
    /// The first failed probe of the run that opened this outage — not when it was declared, which is
    /// later by the probes it took to be sure. Sales from that gap were made while SAP was already gone.
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When enough probes had failed in a row to call it an outage.</summary>
    public DateTime DeclaredAtUtc { get; set; }

    /// <summary>The first successful probe of the run that closed it. Null while SAP is still down.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Why SAP could not be used. See <see cref="SapOutageCauses"/>.</summary>
    [MaxLength(30)]
    public string Cause { get; set; } = SapOutageCauses.Unreachable;

    /// <summary>What the probe that opened it was told.</summary>
    [MaxLength(1000)]
    public string? FirstError { get; set; }

    /// <summary>What the latest failed probe was told, so a long outage shows whether its cause changed.</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>Failed probes counted against this outage, including the ones that declared it.</summary>
    public int FailedProbes { get; set; }

    public DateTime LastProbeAtUtc { get; set; }
}

/// <summary>The values <see cref="SapOutageEntity.Cause"/> takes.</summary>
public static class SapOutageCauses
{
    /// <summary>The probe could not get an answer from SAP.</summary>
    public const string Unreachable = "Unreachable";

    /// <summary>An admin turned the SAP connection off in Settings.</summary>
    public const string SwitchedOff = "SwitchedOff";
}
