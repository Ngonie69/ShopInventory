using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.WarmItemUoms;

/// <summary>
/// Resolves and stores the canonical SAP unit of measure for the item / UoM pairs orders use, so
/// approvals find them already stored.
/// </summary>
/// <remarks>
/// Resolving a UoM costs several SQLQueries round-trips per batch of items against a SAP concurrency
/// limit shared with everything else the process does. Paying that on the approval path is what made
/// approvals take minutes. Resolutions are durable, so this only has real work to do for items nobody
/// has ordered before, or whose UoM could not be resolved last time. Only an admin sends it, from
/// Web → Settings → Data Sync; it ran nightly at 03:30 CAT until that schedule was removed. An approval
/// still resolves a missing pair on demand, so a pass not run costs speed, never a wrong document.
/// </remarks>
public sealed record WarmItemUomsCommand() : IRequest<ErrorOr<ItemUomWarmResult>>;

/// <param name="Pairs">Distinct item / UoM pairs asked about: the last 120 days of orders plus the active catalogue.</param>
/// <param name="Warmed">Pairs in batches SAP answered. Pairs already stored count, at no SAP cost.</param>
/// <param name="FailedBatches">Batches SAP could not answer; their pairs resolve on demand instead.</param>
/// <param name="CompletedAtUtc">When the pass finished.</param>
public sealed record ItemUomWarmResult(int Pairs, int Warmed, int FailedBatches, DateTime CompletedAtUtc);
