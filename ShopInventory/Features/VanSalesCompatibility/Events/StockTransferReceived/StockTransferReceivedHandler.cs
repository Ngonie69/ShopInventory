using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Events.StockTransferReceived;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Events.StockTransferReceived;

/// <summary>
/// Tells a van's handsets that a load has landed on its warehouse, so the van's stock ledger can be
/// brought up to date without the rep having to know to ask.
/// </summary>
/// <remarks>
/// <para><b>Why the handset has to be told.</b> A van's handset keeps its own stock ledger: the server's
/// figure less what it has sold, refreshed against SAP whenever it reads its product catalogue. That
/// refresh already handles a reload correctly — see <c>VanStockReconciliation</c> in the van app — but
/// nothing triggered it. The morning position was taken at the depot, the van drove off, the depot
/// booked a second load (or booked the first one late), and the handset carried a figure below what
/// was physically on the van until the rep happened to pull the stock screen down. Offline, the
/// ledger refuses to sell stock that is visibly on the shelf. This is the trigger: a data-only push
/// the app acts on, with nothing shown to the rep, delivered by FCM when the handset next has signal.</para>
///
/// <para><b>Who is told.</b> The active accounts in the roles that sell off a depot-loaded van
/// (<see cref="ApplicationRoles.DepotLoadedRoles"/>) whose warehouse assignment is the one the stock
/// landed in. A depot controller assigned to the depot, a cart vendor, a shop — none of them run this
/// ledger, so none of them are woken. A warehouse nobody drives simply has no audience.</para>
///
/// <para><b>Once per document.</b> The listener delivers a transfer one line per webhook, all in one
/// poll cycle, so a forty-line load would otherwise be forty high-priority wakes — which is what makes
/// Android start dropping an app's silent messages. The first line of a document signals; the rest,
/// within <see cref="DocumentWindow"/>, are swallowed. The handset re-reads the whole warehouse on a
/// signal, so it needs the fact of the load, not its lines. Held in memory: the window is minutes, and
/// a restart in it costs at worst a second wake.</para>
///
/// <para><b>And the rep is told, separately.</b> The signal stays silent — it is for the ledger, and
/// a tray entry on it would be one per wake. Beside it, each <see cref="ApplicationRoles.Sales"/> rep
/// on the van gets one visible notification per transfer, naming the document, which the handset
/// opens on the transfer itself (<c>NotificationRouting</c> reads <c>InventoryTransfer</c> plus the
/// DocEntry). Sales only: an ADR is woken for the ledger but has no transfers screen to be sent to.
/// The two never share a payload — a visible notice carrying <see cref="ChangeType"/> would be taken
/// by the handset for the signal and shown to nobody. See <see cref="TellRepsAsync"/>.</para>
///
/// <para>Never throws. The ledger row is committed before the event is raised; a signal that could not
/// go out costs the rep a pull-to-refresh, not the stock.</para>
/// </remarks>
public sealed class StockTransferReceivedHandler(
    ApplicationDbContext db,
    IPushNotificationService pushService,
    INotificationService notificationService,
    IMemoryCache cache,
    ILogger<StockTransferReceivedHandler> logger
) : INotificationHandler<StockTransferReceivedEvent>
{
    /// <summary>The <c>changeType</c> the handset matches on. Part of the contract with the van app.</summary>
    public const string ChangeType = "VanStock";

    /// <summary>
    /// The entity the visible notice names. The handset routes a tap on it to the transfer, and the
    /// conversion notice (<c>ConvertTransferRequestHandler</c>) names the same entity the same way,
    /// which is what lets <see cref="TellRepsAsync"/> see that a rep has already been told.
    /// </summary>
    internal const string NoticeEntityType = "InventoryTransfer";

    /// <summary>How long one document's later lines stay silent after its first has signalled.</summary>
    internal static readonly TimeSpan DocumentWindow = TimeSpan.FromMinutes(2);

    private const string SignalledKeyPrefix = "VanStockArrived_";

    public async Task Handle(StockTransferReceivedEvent arrival, CancellationToken cancellationToken)
    {
        try
        {
            var warehouse = arrival.WarehouseCode?.Trim();
            if (string.IsNullOrWhiteSpace(warehouse))
            {
                return;
            }

            var documentKey = DocumentKey(warehouse, arrival.TransferDocEntry);
            if (documentKey is not null && cache.TryGetValue(documentKey, out _))
            {
                return;
            }

            var reps = await RepsDrivingAsync(warehouse, cancellationToken);

            if (reps.Count == 0)
            {
                // Remembered so the remaining lines of a shop's delivery do not each re-run the lookup.
                Remember(documentKey);
                return;
            }

            var sent = await pushService.SendSilentDataToUsersAsync(
                reps.Select(rep => rep.Id).ToList(), Payload(arrival, warehouse), cancellationToken);

            // Before the notice, so a notice that fails cannot bring the rest of the document's lines
            // back round to wake the ledger again.
            Remember(documentKey);

            logger.LogInformation(
                "Signalled a load landing on {Warehouse} (transfer {DocNum}, DocEntry={DocEntry}) to {DeviceCount} device(s) across {RepCount} rep(s)",
                warehouse, arrival.TransferDocNum, arrival.TransferDocEntry, sent, reps.Count);

            await TellRepsAsync(arrival, warehouse, reps, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to signal a load landing on {Warehouse} (DocEntry={DocEntry}); the handset will pick it up on its next catalogue read",
                arrival.WarehouseCode, arrival.TransferDocEntry);
        }
    }

    /// <summary>
    /// The active accounts that sell off this warehouse as a van.
    /// </summary>
    /// <remarks>
    /// Narrowed by role in the query and by warehouse in memory. The assignment is a JSON list in one
    /// column, and <see cref="User.GetWarehouseCodes"/> is the one reader of it — matching the text
    /// with a LIKE would be a second parser of the same column, with its own view of case and spacing.
    /// The roles hold a few dozen accounts, so reading them is cheaper than being wrong about one.
    /// </remarks>
    private async Task<List<User>> RepsDrivingAsync(string warehouse, CancellationToken cancellationToken)
    {
        var roles = ApplicationRoles.DepotLoadedRoles;

        var candidates = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && roles.Contains(user.Role))
            .ToListAsync(cancellationToken);

        return candidates
            .Where(user => user.GetWarehouseCodes()
                .Any(code => string.Equals(code?.Trim(), warehouse, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(user => user.Id)
            .ToList();
    }

    /// <summary>
    /// Tells each Sales rep on the van, in words, that a transfer has landed on it.
    /// </summary>
    /// <remarks>
    /// <para><b>Only with a document entry.</b> That is what the handset opens the transfer by, and
    /// what makes "once per transfer" checkable. A line arriving without one has no transfer to open
    /// and no window to hold it to, so every such line would be its own notice; the silent signal
    /// still goes.</para>
    ///
    /// <para><b>Once per rep per transfer, checked against the stored notices</b> rather than the
    /// in-memory window, because a duplicate here is a buzz in a rep's pocket rather than a wasted
    /// wake: the window does not survive a restart and is not shared across nodes. The same check
    /// skips a rep the conversion notice already reached — a request converted at the depot names
    /// this same transfer to the rep who raised it ("Stock request #N is on its way"), and a SAP
    /// transfer has moved the stock by the time it exists, so that notice already says it.</para>
    ///
    /// <para>Addressed by user id <i>and</i> username. Without the username the live SignalR copy
    /// falls back to the category's audience, and every stock controller with the portal open would
    /// see a toast about somebody else's van.</para>
    ///
    /// <para>Each rep on their own try: one notice that cannot be stored must not cost the next rep
    /// theirs, and none of it can undo the ledger or the signal.</para>
    /// </remarks>
    private async Task TellRepsAsync(
        StockTransferReceivedEvent arrival,
        string warehouse,
        IReadOnlyCollection<User> reps,
        CancellationToken cancellationToken)
    {
        if (arrival.TransferDocEntry is not int docEntry)
        {
            return;
        }

        var sales = reps
            .Where(rep => string.Equals(rep.Role?.Trim(), ApplicationRoles.Sales, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sales.Count == 0)
        {
            return;
        }

        var entityId = docEntry.ToString();
        var salesIds = sales.Select(rep => (Guid?)rep.Id).ToList();

        var alreadyTold = await db.Notifications
            .AsNoTracking()
            .Where(notice => notice.EntityType == NoticeEntityType
                && notice.EntityId == entityId
                && salesIds.Contains(notice.UserId))
            .Select(notice => notice.UserId)
            .ToListAsync(cancellationToken);

        foreach (var rep in sales.Where(rep => !alreadyTold.Contains(rep.Id)))
        {
            try
            {
                await notificationService.CreateNotificationAsync(Notice(arrival, warehouse, docEntry, rep), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not tell {Username} that transfer {DocNum} (DocEntry={DocEntry}) landed on {Warehouse}; the ledger signal went",
                    rep.Username, arrival.TransferDocNum, docEntry, warehouse);
            }
        }
    }

    /// <summary>
    /// The visible notice. Document-level for the same reason as <see cref="Payload"/>: it is raised
    /// on the transfer's first line, so naming that line would misdescribe the load.
    /// </summary>
    private static CreateNotificationRequest Notice(StockTransferReceivedEvent arrival, string warehouse, int docEntry, User rep)
    {
        var docNum = arrival.TransferDocNum?.ToString() ?? docEntry.ToString();
        var source = string.IsNullOrWhiteSpace(arrival.SourceWarehouse) ? "the depot" : arrival.SourceWarehouse.Trim();

        return new CreateNotificationRequest
        {
            Title = $"Stock loaded onto {warehouse}",
            Message = $"Transfer #{docNum} from {source} has landed on your van. Tap to see what came in.",
            Type = "Success",
            Category = "InventoryTransfer",
            EntityType = NoticeEntityType,
            EntityId = docEntry.ToString(),
            ActionUrl = "/inventory-transfers",
            TargetUserId = rep.Id,
            TargetUsername = rep.Username,
            Data = new Dictionary<string, string>
            {
                ["transferDocEntry"] = docEntry.ToString(),
                ["transferDocNum"] = docNum,
                ["fromWarehouse"] = source,
                ["toWarehouse"] = warehouse
            }
        };
    }

    /// <summary>
    /// What the handset is handed. Document-level on purpose: one signal stands for every line of the
    /// transfer, so naming the line that happened to arrive first would misdescribe the load.
    /// </summary>
    private static Dictionary<string, string> Payload(StockTransferReceivedEvent arrival, string warehouse)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeType"] = ChangeType,
            ["warehouseCode"] = warehouse,
            ["fromWarehouse"] = arrival.SourceWarehouse?.Trim() ?? string.Empty,
            ["changedAtUtc"] = arrival.ReceivedAtUtc.ToString("O")
        };

        if (arrival.TransferDocEntry is int docEntry)
        {
            data["transferDocEntry"] = docEntry.ToString();
        }

        if (arrival.TransferDocNum is int docNum)
        {
            data["transferDocNum"] = docNum.ToString();
        }

        return data;
    }

    private static string? DocumentKey(string warehouse, int? transferDocEntry) =>
        transferDocEntry is int docEntry
            ? $"{SignalledKeyPrefix}{warehouse.ToUpperInvariant()}_{docEntry}"
            : null;

    private void Remember(string? documentKey)
    {
        if (documentKey is not null)
        {
            cache.Set(documentKey, true, DocumentWindow);
        }
    }
}
