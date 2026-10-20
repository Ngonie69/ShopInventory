using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Components;
using ShopInventory.Web.Features.FactoryTransfers.Commands.CancelFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Commands.DecideFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Commands.RecallFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Commands.RetryFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Commands.SendFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfer;
using ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;
using ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfers;
using ShopInventory.Web.Features.FactoryTransfers.Queries.GetDepotOrders;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Production releasing stock to dispatch, and dispatch returning it, through the holding warehouse.
/// The sender posts it into FSRW; the receiving side counts what arrived and accepts it, part-accepts
/// it or rejects it, and whatever is not accepted goes back.
/// </summary>
/// <remarks>
/// Every rule — which warehouses a person may send from, who may accept, the reason a short count
/// needs — is the API's; the page only offers what the API says the caller may do (the summary's
/// <c>Can…</c> flags and the options' routes) and reloads from the API after every action.
/// </remarks>
public partial class FactoryTransfers : IDisposable
{
    private const int PageSize = 25;
    private const string Awaiting = "awaiting";
    private const string Open = "open";
    private const string All = "all";

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private static readonly (string Value, string Label)[] Tabs =
    [
        (Awaiting, "Waiting for me"),
        (Open, "Open"),
        (All, "All")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IWarehouseStockCacheService StockCache { get; set; } = default!;

    private readonly CancellationTokenSource disposal = new();

    private FactoryTransferOptionsDto? options;
    private List<FactoryTransferSummaryDto> transfers = [];
    private int totalCount;
    private int awaitingCount;
    private int currentPage = 1;
    private bool isLoading = true;
    private string? errorMessage;
    private string view = Awaiting;
    private string? searchText;
    private int searchVersion;

    private int? detailId;
    private FactoryTransferDetailDto? detail;
    private bool drawerOpen;
    private bool isLoadingDetail;
    private string? detailError;
    private string? decisionNote;
    private bool noteNeeded;
    private readonly Dictionary<int, decimal> counts = [];

    private bool isSubmitting;
    private string? submittingAction;

    private bool sendOpen;
    private string? sendSource;
    private string? sendDestination;
    private string? sendReason;
    private string? sendError;
    private string clientRequestId = Guid.NewGuid().ToString("N");
    private string? pickerValue;
    private bool isLoadingStock;
    private List<NocturnePickerOption> stockOptions = [];
    private readonly Dictionary<string, StockRow> stockByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SendLine> sendLines = [];
    private string? sendPlate;
    private string? sendDriver;
    private List<DepotOrderDto> depotOrders = [];
    private string? selectedOrder;
    private bool isLoadingOrders;

    private string HoldingWarehouse => options?.HoldingWarehouseCode is { Length: > 0 } code ? code : "FSRW";

    private bool CanSend => options is not null && (options.Routes.Count > 0 || options.DepotDelivery is { DispatchWarehouses.Count: > 0 });

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));

    private string RangeLabel
    {
        get
        {
            var first = (currentPage - 1) * PageSize + 1;
            var last = Math.Min(currentPage * PageSize, totalCount);
            return $"{first}–{last} of {totalCount:N0}";
        }
    }

    private string EmptyMessage => view switch
    {
        Awaiting => "Nothing is waiting for you to accept.",
        Open => "No transfers are open.",
        _ => string.IsNullOrWhiteSpace(searchText) ? "No factory transfers yet." : "No transfers match that search."
    };

    protected override async Task OnInitializedAsync()
    {
        var loaded = await Mediator.Send(new GetFactoryTransferOptionsQuery(), disposal.Token);
        if (loaded.IsError)
            errorMessage = loaded.FirstError.Description;
        else
        {
            options = loaded.Value;
            // A sender who accepts nothing has no queue of their own; their open sends are the view that matters.
            if (!options.CanAccept && !options.CanAcceptAny)
                view = Open;
        }

        await LoadAsync();
    }

    // ── The list ─────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        isLoading = true;
        try
        {
            var result = await Mediator.Send(
                new GetFactoryTransfersQuery(view, searchText, currentPage, PageSize), disposal.Token);
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            transfers = result.Value.Items;
            totalCount = result.Value.TotalCount;
            awaitingCount = result.Value.AwaitingMeCount;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    private int? CountFor(string tab)
        => tab == Awaiting ? awaitingCount : tab == view ? totalCount : null;

    private async Task SetViewAsync(string value)
    {
        if (view == value)
            return;
        view = value;
        currentPage = 1;
        await LoadAsync();
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task OnSearchInputAsync(ChangeEventArgs args)
    {
        searchText = args.Value?.ToString();
        var version = ++searchVersion;
        try { await Task.Delay(SearchDebounce, disposal.Token); }
        catch (OperationCanceledException) { return; }
        if (version != searchVersion)
            return;
        currentPage = 1;
        await LoadAsync();
    }

    private async Task ClearSearchAsync()
    {
        searchText = null;
        searchVersion++;
        currentPage = 1;
        await LoadAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1) return;
        currentPage--;
        await LoadAsync();
    }

    private async Task NextPageAsync()
    {
        if (currentPage >= TotalPages) return;
        currentPage++;
        await LoadAsync();
    }

    // ── The open transfer ────────────────────────────────────────────────────────

    private async Task PickAsync(int id)
    {
        detailId = id;
        drawerOpen = true;
        await LoadDetailAsync(id);
    }

    private async Task LoadDetailAsync(int id)
    {
        isLoadingDetail = true;
        detail = null;
        detailError = null;
        decisionNote = null;
        noteNeeded = false;
        counts.Clear();
        try
        {
            var result = await Mediator.Send(new GetFactoryTransferQuery(id), disposal.Token);
            if (result.IsError)
                detailError = result.FirstError.Description;
            else
                detail = result.Value;
        }
        finally
        {
            isLoadingDetail = false;
        }
    }

    private void CloseDrawer()
    {
        if (isSubmitting) return;
        drawerOpen = false;
        detail = null;
        detailId = null;
    }

    private decimal? CountFor(FactoryTransferLineDto line)
        => counts.TryGetValue(line.Id, out var value) ? value : null;

    private string CountText(int lineId)
        => counts.TryGetValue(lineId, out var value) ? QuantityDisplay.Format(value) : string.Empty;

    private void SetCount(FactoryTransferLineDto line, object? raw)
    {
        var text = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            counts.Remove(line.Id);
            return;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0)
            counts[line.Id] = Math.Min(value, line.Quantity);
    }

    private void StepCount(FactoryTransferLineDto line, int step)
    {
        var current = CountFor(line) ?? (step > 0 ? 0 : line.Quantity);
        counts[line.Id] = Math.Clamp(current + step, 0, line.Quantity);
    }

    private void FillWithSent()
    {
        foreach (var line in detail?.Lines ?? [])
            counts.TryAdd(line.Id, line.Quantity);
    }

    private bool AllCounted => detail?.Lines.All(line => counts.ContainsKey(line.Id)) == true;

    private bool AllCountedAsSent => detail?.Lines.All(line => CountFor(line) == line.Quantity) == true;

    private bool IsShort => detail?.Lines.Any(line => CountFor(line) < line.Quantity) == true;

    private bool CanSubmitCount => AllCounted && detail!.Lines.Any(line => CountFor(line) > 0);

    private string AcceptLabel
    {
        get
        {
            if (detail is null || !AllCounted) return "Count every line";
            if (!IsShort) return "Accept all";
            var accepted = detail.Lines.Sum(line => CountFor(line) ?? 0);
            return $"Accept {QuantityDisplay.Format(accepted)} of {QuantityDisplay.Format(detail.Summary.TotalQuantity)}";
        }
    }

    private string NoteHint => detail?.Summary.CanDecide == true
        ? (IsShort ? "required: say what is short and why" : "required to reject")
        : "required";

    private string HeadlineNote => detail?.Summary switch
    {
        { CanDecide: true } summary => $"sent by {summary.SentByName}, waiting in {summary.HoldingWarehouseCode} for you to count",
        { Status: FactoryTransferStatus.InTransit } summary => $"in {summary.HoldingWarehouseCode}, waiting for {summary.DestinationWarehouseCode}",
        { Status: FactoryTransferStatus.Accepted } summary => $"accepted into {summary.DestinationWarehouseCode}",
        { Status: FactoryTransferStatus.PartiallyAccepted } summary => $"part-accepted into {summary.DestinationWarehouseCode}, the rest back to {summary.SourceWarehouseCode}",
        { Status: FactoryTransferStatus.Rejected } summary => $"rejected, back in {summary.SourceWarehouseCode}",
        { Status: FactoryTransferStatus.Recalled } summary => $"recalled to {summary.SourceWarehouseCode}",
        { Status: FactoryTransferStatus.Cancelled } => "cancelled before it reached SAP; nothing moved",
        _ => "posting to SAP"
    };

    private async Task AcceptAsync()
    {
        if (detail is null || !CanSubmitCount) return;
        if (IsShort && string.IsNullOrWhiteSpace(decisionNote))
        {
            noteNeeded = true;
            return;
        }

        var request = new DecideFactoryTransferRequestDto
        {
            Decision = "Accept",
            Reason = Blank(decisionNote),
            Lines = detail.Lines.Select(line => new DecideFactoryTransferLineDto
            {
                LineId = line.Id,
                AcceptedQuantity = CountFor(line) ?? 0
            }).ToList()
        };
        await RunAsync("accept", () => Mediator.Send(new DecideFactoryTransferCommand(detail.Summary.Id, request)));
    }

    private async Task RejectAsync()
    {
        if (detail is null) return;
        if (string.IsNullOrWhiteSpace(decisionNote))
        {
            noteNeeded = true;
            return;
        }

        var request = new DecideFactoryTransferRequestDto { Decision = "Reject", Reason = decisionNote!.Trim() };
        await RunAsync("reject", () => Mediator.Send(new DecideFactoryTransferCommand(detail.Summary.Id, request)));
    }

    private async Task RecallAsync()
    {
        if (detail is null) return;
        if (string.IsNullOrWhiteSpace(decisionNote))
        {
            noteNeeded = true;
            return;
        }

        await RunAsync("recall", () => Mediator.Send(new RecallFactoryTransferCommand(detail.Summary.Id, decisionNote!)));
    }

    private Task RetryAsync()
        => detail is null ? Task.CompletedTask : RunAsync("retry", () => Mediator.Send(new RetryFactoryTransferCommand(detail.Summary.Id)));

    private Task CancelAsync()
        => detail is null ? Task.CompletedTask : RunAsync("cancel", () => Mediator.Send(new CancelFactoryTransferCommand(detail.Summary.Id)));

    /// <summary>Runs an action, says what happened, and reloads the transfer and the list from the API.</summary>
    private async Task RunAsync(string action, Func<Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>>> run)
    {
        if (detail is null) return;
        var id = detail.Summary.Id;
        isSubmitting = true;
        submittingAction = action;
        try
        {
            var result = await run();
            if (result.IsError)
            {
                await LoadDetailAsync(id);
                detailError = result.FirstError.Description;
            }
            else
            {
                Snackbar.Add(result.Value.Message, Severity.Success);
                await LoadDetailAsync(id);
            }

            await LoadAsync();
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }
    }

    // ── Sending ──────────────────────────────────────────────────────────────────

    /// <summary>The From choice that means "load a truck for a depot" rather than a warehouse.</summary>
    private const string TruckSource = "__truck";

    private bool IsTruck => sendSource == TruckSource;

    private FactoryTransferRouteDto? SelectedRoute
        => options?.Routes.FirstOrDefault(route => route.SourceWarehouseCode == sendSource);

    private bool ShowLineSources => detail?.Summary.Direction == FactoryTransferStatus.Delivery;

    private IReadOnlyList<INocturneSelectOption<string>> SourceOptions
    {
        get
        {
            var list = new List<INocturneSelectOption<string>>();
            if (options?.DepotDelivery is { DispatchWarehouses.Count: > 0 })
                list.Add(new NocturneSelectOption<string>(TruckSource, "Load a truck for a depot", "accent") { RuleAfter = options.Routes.Count > 0 });
            list.AddRange((options?.Routes ?? []).Select(route => (INocturneSelectOption<string>)new NocturneSelectOption<string>(
                route.SourceWarehouseCode,
                $"{route.SourceWarehouseCode} · {route.DepartmentName} {DirectionWord(route.Direction)}")));
            return list;
        }
    }

    private IReadOnlyList<INocturneSelectOption<string>> DestinationOptions
        => IsTruck
            ? (options?.DepotDelivery?.Depots ?? [])
                .Select(depot => (INocturneSelectOption<string>)new NocturneSelectOption<string>(depot.Code, $"{depot.Code} · {depot.Name}"))
                .ToList()
            : SelectedRoute?.DestinationWarehouseCodes
                .Select(code => (INocturneSelectOption<string>)new NocturneSelectOption<string>(code, code))
                .ToList() ?? [];

    private IReadOnlyList<INocturneSelectOption<string>> OrderOptions
        => depotOrders
            .Select(order => (INocturneSelectOption<string>)new NocturneSelectOption<string>(
                order.DocEntry.ToString(CultureInfo.InvariantCulture),
                $"#{order.DocNum} · {ShortDate(order.DocDate)} · {OrderSummary(order)}"))
            .Prepend(new NocturneSelectOption<string>(string.Empty, "No order — load it by hand", "neutral") { RuleAfter = true, IsUnset = true })
            .ToList();

    /// <summary>Where the stock waits: DEPOTRAN for a truck or a depot return, FSRW otherwise.</summary>
    private string SendHolding
        => IsTruck || SelectedRoute?.Direction == FactoryTransferStatus.DepotReturn
            ? options?.DepotDelivery?.HoldingWarehouseCode is { Length: > 0 } depot ? depot : "DEPOTRAN"
            : HoldingWarehouse;

    private string SendTitle
    {
        get
        {
            if (IsTruck)
            {
                var depot = options?.DepotDelivery?.Depots.FirstOrDefault(item => item.Code == sendDestination);
                return depot is null ? "Truck to a depot" : $"Truck to {depot.Name}";
            }

            return SelectedRoute is { } route ? $"{route.DepartmentName} {DirectionWord(route.Direction)}" : "Send stock";
        }
    }

    private string SendRoute
    {
        get
        {
            if (string.IsNullOrEmpty(sendSource))
                return "Choose where it comes from";
            var from = IsTruck
                ? sendLines.Count == 0 ? "dispatch" : string.Join(", ", sendLines.Select(line => line.FromWarehouse).Distinct())
                : sendSource;
            return $"{from} → {SendHolding} → {(string.IsNullOrEmpty(sendDestination) ? "…" : sendDestination)}";
        }
    }

    private void OpenSend()
    {
        sendOpen = true;
        sendError = null;
        if (sendSource is null && options is { Routes.Count: 1, DepotDelivery: null })
            _ = OnSourceChangedAsync(options.Routes[0].SourceWarehouseCode);
    }

    private void CloseSend()
    {
        if (isSubmitting) return;
        sendOpen = false;
    }

    private async Task OnSourceChangedAsync(string? source)
    {
        if (string.Equals(sendSource, source, StringComparison.Ordinal))
            return;

        sendSource = source;
        sendDestination = null;
        depotOrders = [];
        selectedOrder = null;
        sendLines.Clear();
        stockOptions = [];
        stockByKey.Clear();
        if (string.IsNullOrEmpty(source))
            return;

        if (!IsTruck)
        {
            var destinations = SelectedRoute?.DestinationWarehouseCodes ?? [];
            sendDestination = destinations.Count == 1 ? destinations[0] : null;
        }

        // A truck loads from every dispatch warehouse on the account; anything else from its source.
        var warehouses = IsTruck ? options?.DepotDelivery?.DispatchWarehouses ?? [] : [source];

        isLoadingStock = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            foreach (var warehouse in warehouses)
            {
                var stock = await StockCache.GetAllCachedStockAsync(warehouse);
                // A late answer for a form that has since moved off this source is dropped.
                if (!string.Equals(sendSource, source, StringComparison.Ordinal))
                    return;

                foreach (var product in stock?.Products ?? [])
                {
                    if (string.IsNullOrWhiteSpace(product.ItemCode) || product.QuantityInStock <= 0)
                        continue;
                    stockByKey[StockKey(warehouse, product.ItemCode)] = new StockRow(warehouse, product);
                }
            }

            stockOptions = stockByKey
                .OrderBy(entry => entry.Value.Product.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Value.Warehouse, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new NocturnePickerOption(
                    entry.Key,
                    entry.Value.Product.ItemName ?? entry.Value.Product.ItemCode!,
                    IsTruck
                        ? $"{entry.Value.Warehouse} · {entry.Value.Product.ItemCode} · {QuantityDisplay.Format(entry.Value.Product.QuantityInStock)} in stock"
                        : $"{entry.Value.Product.ItemCode} · {QuantityDisplay.Format(entry.Value.Product.QuantityInStock)} in stock"))
                .ToList();
        }
        catch (Exception)
        {
            sendError = $"The stock in {string.Join(", ", warehouses)} could not be loaded. Refresh and try again.";
        }
        finally
        {
            isLoadingStock = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnDepotChangedAsync(string? depot)
    {
        sendDestination = depot;
        depotOrders = [];
        selectedOrder = null;
        if (string.IsNullOrEmpty(depot))
            return;

        isLoadingOrders = true;
        try
        {
            var result = await Mediator.Send(new GetDepotOrdersQuery(depot), disposal.Token);
            // Orders are a convenience; a truck can always be loaded by hand.
            if (!result.IsError && sendDestination == depot)
                depotOrders = result.Value;
        }
        finally
        {
            isLoadingOrders = false;
        }
    }

    /// <summary>
    /// Fills the lines from a depot order. Each item is taken from the dispatch warehouse holding the
    /// most of it; one no dispatch warehouse holds is named rather than silently left off.
    /// </summary>
    private void OnOrderChanged(string? docEntry)
    {
        selectedOrder = string.IsNullOrEmpty(docEntry) ? null : docEntry;
        var order = depotOrders.FirstOrDefault(item => item.DocEntry.ToString(CultureInfo.InvariantCulture) == selectedOrder);
        if (order is null)
            return;

        sendLines.Clear();
        var missing = new List<string>();
        foreach (var wanted in order.Lines)
        {
            var best = stockByKey.Values
                .Where(row => string.Equals(row.Product.ItemCode, wanted.ItemCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.Product.QuantityInStock)
                .FirstOrDefault();
            if (best is null)
            {
                missing.Add(wanted.ItemCode);
                continue;
            }

            if (sendLines.Any(line => line.Key == StockKey(best.Warehouse, wanted.ItemCode)))
                continue;
            var line = NewLine(best);
            SetSendQuantity(line, wanted.Quantity.ToString(CultureInfo.InvariantCulture));
            sendLines.Add(line);
        }

        if (string.IsNullOrWhiteSpace(sendReason))
            sendReason = string.IsNullOrWhiteSpace(order.Comments) ? $"Depot order #{order.DocNum}" : order.Comments.Trim();
        sendError = missing.Count == 0
            ? null
            : $"Not in any dispatch warehouse, so left off: {string.Join(", ", missing)}.";
    }

    private void AddLine(string? key)
    {
        pickerValue = null;
        if (string.IsNullOrEmpty(key) || !stockByKey.TryGetValue(key, out var row))
            return;
        if (sendLines.Any(line => line.Key == key))
            return;

        sendLines.Add(NewLine(row));
    }

    private static SendLine NewLine(StockRow row)
        => new(row.Warehouse, row.Product.ItemCode!, row.Product.ItemName ?? row.Product.ItemCode!, row.Product.UoM, row.Product.QuantityInStock);

    private static void SetSendQuantity(SendLine line, object? raw)
    {
        var text = raw?.ToString()?.Trim();
        line.QuantityText = text ?? string.Empty;
        line.Quantity = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    private bool TruckDetailsGiven => !IsTruck || (!string.IsNullOrWhiteSpace(sendPlate) && !string.IsNullOrWhiteSpace(sendDriver));

    private bool CanSubmitSend
        => !string.IsNullOrEmpty(sendSource)
            && !string.IsNullOrEmpty(sendDestination)
            && !string.IsNullOrWhiteSpace(sendReason)
            && TruckDetailsGiven
            && sendLines.Count > 0
            && sendLines.All(line => line.Quantity > 0 && line.Quantity <= line.Available);

    private string SendHint
    {
        get
        {
            if (string.IsNullOrEmpty(sendSource)) return "Choose where it comes from.";
            if (string.IsNullOrEmpty(sendDestination)) return IsTruck ? "Choose the depot." : "Choose where it goes.";
            if (!TruckDetailsGiven) return "Give the truck's registration and the driver.";
            if (sendLines.Count == 0) return "Add at least one product.";
            if (sendLines.Any(line => line.Quantity is null)) return "Enter a quantity on every line.";
            if (sendLines.Any(line => line.Quantity > line.Available)) return "A quantity is more than is in stock.";
            if (string.IsNullOrWhiteSpace(sendReason)) return "Say why it is being sent.";
            return $"{sendLines.Count} product(s) to {SendHolding}, for {sendDestination}.";
        }
    }

    private async Task SendAsync()
    {
        if (!CanSubmitSend) return;
        isSubmitting = true;
        submittingAction = "send";
        sendError = null;
        try
        {
            var request = new SendFactoryTransferRequestDto
            {
                ClientRequestId = clientRequestId,
                SourceWarehouseCode = IsTruck ? sendLines[0].FromWarehouse : sendSource!,
                DestinationWarehouseCode = sendDestination!,
                Reason = sendReason!.Trim(),
                VehiclePlate = IsTruck ? sendPlate!.Trim() : null,
                DriverName = IsTruck ? sendDriver!.Trim() : null,
                SourceRequestDocEntry = IsTruck && int.TryParse(selectedOrder, out var order) ? order : null,
                Lines = sendLines.Select(line => new SendFactoryTransferLineDto
                {
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemName,
                    UoMCode = line.UoM,
                    FromWarehouseCode = IsTruck ? line.FromWarehouse : null,
                    Quantity = line.Quantity!.Value
                }).ToList()
            };

            var result = await Mediator.Send(new SendFactoryTransferCommand(request));
            if (result.IsError)
            {
                sendError = result.FirstError.Description;
                // A send that was saved but not posted is a record now; the next press must not make a second one.
                if (sendError.Contains("was saved but not sent", StringComparison.Ordinal))
                    ResetSendForm();
                await LoadAsync();
                return;
            }

            Snackbar.Add(result.Value.Message, Severity.Success);
            ResetSendForm();
            sendOpen = false;
            view = Open;
            currentPage = 1;
            await LoadAsync();
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }
    }

    private void ResetSendForm()
    {
        clientRequestId = Guid.NewGuid().ToString("N");
        sendLines.Clear();
        sendReason = null;
        selectedOrder = null;
        sendPlate = null;
        sendDriver = null;
    }

    private static string StockKey(string warehouse, string itemCode) => $"{warehouse}|{itemCode}";

    private static string ShortDate(string sapDate)
        => DateTime.TryParse(sapDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd MMM", CultureInfo.InvariantCulture)
            : sapDate;

    private static string OrderSummary(DepotOrderDto order)
    {
        var products = order.Lines.Count == 1 ? "1 product" : $"{order.Lines.Count} products";
        var note = order.Comments.Split('|').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0);
        return string.IsNullOrWhiteSpace(note) ? products : $"{products} · {(note.Length > 40 ? note[..40] + "…" : note)}";
    }

    // ── Words and colours ────────────────────────────────────────────────────────

    private static string DirectionWord(string direction) => direction switch
    {
        FactoryTransferStatus.Return or FactoryTransferStatus.DepotReturn => "return",
        FactoryTransferStatus.Delivery => "delivery",
        _ => "release"
    };

    private static string StatusLabel(string status) => status switch
    {
        FactoryTransferStatus.Sending => "Sending",
        FactoryTransferStatus.SendFailed => "Send failed",
        FactoryTransferStatus.InTransit => "In transit",
        FactoryTransferStatus.Settling => "Posting",
        FactoryTransferStatus.SettleFailed => "Post failed",
        FactoryTransferStatus.Accepted => "Accepted",
        FactoryTransferStatus.PartiallyAccepted => "Part-accepted",
        FactoryTransferStatus.Rejected => "Rejected",
        FactoryTransferStatus.Recalled => "Recalled",
        FactoryTransferStatus.Cancelled => "Cancelled",
        _ => status
    };

    private static string StatusModifier(string status) => status switch
    {
        FactoryTransferStatus.InTransit => "is-accent",
        FactoryTransferStatus.Sending or FactoryTransferStatus.Settling => "is-info",
        FactoryTransferStatus.Accepted or FactoryTransferStatus.PartiallyAccepted => "is-good",
        FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed => "is-bad",
        _ => "is-neutral"
    };

    private static string StatusFamily(string status) => status switch
    {
        FactoryTransferStatus.InTransit => "ops-fam-accent",
        FactoryTransferStatus.Sending or FactoryTransferStatus.Settling => "ops-fam-info",
        FactoryTransferStatus.Accepted => "ops-fam-good",
        FactoryTransferStatus.PartiallyAccepted => "ops-fam-warn",
        FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed => "ops-fam-bad",
        _ => "ops-fam-neutral"
    };

    private static string SummaryModifier(FactoryTransferSummaryDto transfer) => transfer.Status switch
    {
        FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed => "is-bad",
        FactoryTransferStatus.Sending or FactoryTransferStatus.Settling => "is-info",
        _ => ""
    };

    private static string RowSummary(FactoryTransferSummaryDto transfer)
    {
        var products = transfer.LineCount == 1 ? "1 product" : $"{transfer.LineCount} products";
        return transfer.Status switch
        {
            FactoryTransferStatus.InTransit when transfer.CanDecide => $"{products} · yours to count",
            FactoryTransferStatus.InTransit => $"{products} · waiting for {transfer.DestinationWarehouseCode}",
            FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed => $"{products} · needs a retry",
            _ when transfer.DecidedByName is { Length: > 0 } by => $"{products} · {StatusLabel(transfer.Status).ToLowerInvariant()} by {by}",
            _ => products
        };
    }

    private static string DecidedLabel(FactoryTransferSummaryDto summary) => summary.Status switch
    {
        FactoryTransferStatus.Rejected => "Rejected by",
        FactoryTransferStatus.Recalled => "Recalled by",
        FactoryTransferStatus.Cancelled => "Cancelled by",
        FactoryTransferStatus.PartiallyAccepted => "Part-accepted by",
        _ => summary.Decision == "Rejected" ? "Rejected by" : summary.Decision == "Recalled" ? "Recalled by" : "Accepted by"
    };

    private static string DoneLabel(FactoryTransferSummaryDto summary) => summary.Status switch
    {
        FactoryTransferStatus.InTransit => $"Waiting for {summary.DestinationWarehouseCode} to accept it.",
        _ when !string.IsNullOrEmpty(summary.Status) && IsFinal(summary.Status) => "Settled. Nothing more to do.",
        _ => "Nothing for you to do on this one."
    };

    private static bool IsFinal(string status) => status is FactoryTransferStatus.Accepted
        or FactoryTransferStatus.PartiallyAccepted or FactoryTransferStatus.Rejected
        or FactoryTransferStatus.Recalled or FactoryTransferStatus.Cancelled;

    private static string UnitsText(decimal units) => units == 1 ? "1 unit" : $"{QuantityDisplay.Format(units)} units";

    private static string FormatStamp(DateTime utc) => IAuditService.ToCAT(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
        .ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }

    private sealed record StockRow(string Warehouse, ProductDto Product);

    private sealed class SendLine(string fromWarehouse, string itemCode, string itemName, string? uoM, decimal available)
    {
        public string FromWarehouse { get; } = fromWarehouse;
        public string Key => StockKey(FromWarehouse, ItemCode);
        public string ItemCode { get; } = itemCode;
        public string ItemName { get; } = itemName;
        public string? UoM { get; } = uoM;
        public decimal Available { get; } = available;
        public decimal? Quantity { get; set; }
        public string QuantityText { get; set; } = string.Empty;
    }
}
