using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class ExceptionCenter
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private ILogger<ExceptionCenter> Logger { get; set; } = default!;

    private ExceptionCenterDashboardModel? dashboard;
    private bool isLoading = true;
    private bool hasInitialized;
    private bool hasLoggedView;
    private string? errorMessage;
    private string? successMessage;
    private string currentUsername = string.Empty;
    private string searchText = string.Empty;
    private string selectedSource = "All";
    private string selectedCategory = "All";
    private string selectedStatus = "All";
    private string selectedAssignment = "All";
    private string selectedAcknowledgement = "All";
    private readonly HashSet<string> acknowledgingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> assigningKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> retryingKeys = new(StringComparer.OrdinalIgnoreCase);

    private List<ExceptionCenterItemModel> Items => dashboard?.Items ?? new List<ExceptionCenterItemModel>();
    private List<ExceptionCenterItemModel> FilteredItems => ApplyFilters().ToList();
    private List<string> SourceOptions => BuildOptions(Items.Select(item => item.Source));
    private List<string> CategoryOptions => BuildOptions(Items.Select(item => item.Category));
    private List<string> StatusOptions => BuildOptions(Items.Select(item => item.Status));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
        {
            return;
        }

        hasInitialized = true;
        await ResolveCurrentUserAsync();
        await LoadDashboardAsync();
        StateHasChanged();
    }

    private async Task LoadDashboardAsync()
    {
        isLoading = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new GetExceptionCenterQuery());

            result.SwitchFirst(
                value => dashboard = value,
                error => errorMessage = error.Description);

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewExceptionCenter, "ExceptionCenter", null, "Viewed integration exception queue", true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load exception center page");
            errorMessage = "Failed to load exception center.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task ReloadAsync()
    {
        await LoadDashboardAsync();
    }

    private async Task RetryItemAsync(ExceptionCenterItemModel item)
    {
        var retryKey = GetItemKey(item);
        if (!retryingKeys.Add(retryKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new RetryExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.RetryExceptionCenterItem, "ExceptionCenter", retryKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Retry queued for {item.Reference}.";
            await AuditService.LogAsync(AuditActions.RetryExceptionCenterItem, "ExceptionCenter", retryKey, $"Retried {item.Reference}", true);
            await LoadDashboardAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retry exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Retry request failed.";
        }
        finally
        {
            retryingKeys.Remove(retryKey);
        }
    }

    private async Task AcknowledgeItemAsync(ExceptionCenterItemModel item)
    {
        var itemKey = GetItemKey(item);
        if (!acknowledgingKeys.Add(itemKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new AcknowledgeExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.AcknowledgeExceptionCenterItem, "ExceptionCenter", itemKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Acknowledged {item.Reference}.";
            await AuditService.LogAsync(AuditActions.AcknowledgeExceptionCenterItem, "ExceptionCenter", itemKey, $"Acknowledged {item.Reference}", true);
            await LoadDashboardAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to acknowledge exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Acknowledge request failed.";
        }
        finally
        {
            acknowledgingKeys.Remove(itemKey);
        }
    }

    private async Task AssignItemAsync(ExceptionCenterItemModel item)
    {
        var itemKey = GetItemKey(item);
        if (!assigningKeys.Add(itemKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new AssignExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.AssignExceptionCenterItem, "ExceptionCenter", itemKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Assigned {item.Reference} to {currentUsername}.";
            await AuditService.LogAsync(AuditActions.AssignExceptionCenterItem, "ExceptionCenter", itemKey, $"Assigned {item.Reference} to {currentUsername}", true);
            await LoadDashboardAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to assign exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Assign request failed.";
        }
        finally
        {
            assigningKeys.Remove(itemKey);
        }
    }

    private async Task ResolveCurrentUserAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        currentUsername = authState.User.Identity?.Name ?? string.Empty;
    }

    private IEnumerable<ExceptionCenterItemModel> ApplyFilters()
    {
        IEnumerable<ExceptionCenterItemModel> query = Items;

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(item => MatchesSearch(item, searchText));
        }

        if (!string.Equals(selectedSource, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Source, selectedSource, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(selectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Category, selectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(selectedStatus, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Status, selectedStatus, StringComparison.OrdinalIgnoreCase));
        }

        query = selectedAssignment switch
        {
            "Unassigned" => query.Where(item => string.IsNullOrWhiteSpace(item.AssignedToUsername)),
            "Assigned" => query.Where(item => !string.IsNullOrWhiteSpace(item.AssignedToUsername)),
            "Mine" => query.Where(item => string.Equals(item.AssignedToUsername, currentUsername, StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        query = selectedAcknowledgement switch
        {
            "Acknowledged" => query.Where(item => item.IsAcknowledged),
            "Unacknowledged" => query.Where(item => !item.IsAcknowledged),
            _ => query
        };

        return query;
    }

    private void ResetFilters()
    {
        searchText = string.Empty;
        selectedSource = "All";
        selectedCategory = "All";
        selectedStatus = "All";
        selectedAssignment = "All";
        selectedAcknowledgement = "All";
    }

    private bool CanAssignToCurrentUser(ExceptionCenterItemModel item)
        => !string.IsNullOrWhiteSpace(currentUsername)
           && !string.Equals(item.AssignedToUsername, currentUsername, StringComparison.OrdinalIgnoreCase);

    private static string GetItemKey(ExceptionCenterItemModel item) => $"{item.Source}:{item.ItemId}";

    private string FormatDateTime(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue)
        {
            return "-";
        }

        return $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM yyyy HH:mm} CAT";
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string GetStatusBadgeClass(string status)
        => status switch
        {
            "RequiresReview" => "exc-badge-danger",
            "Failed" => "exc-badge-warn",
            _ => "exc-badge-neutral"
        };

    private static string Shorten(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= limit)
        {
            return value ?? string.Empty;
        }

        return value[..limit] + "...";
    }

    private static bool MatchesSearch(ExceptionCenterItemModel item, string search)
    {
        return Contains(item.Reference, search)
               || Contains(item.Title, search)
               || Contains(item.LastError, search)
               || Contains(item.Source, search)
               || Contains(item.Category, search)
               || Contains(item.AssignedToUsername, search);
    }

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildOptions(IEnumerable<string> values)
        => new[] { "All" }
            .Concat(values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToList();
}