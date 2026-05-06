using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using ShopInventory.Web.Features.UserManagement.Commands.RefreshDriverBusinessPartnerAccess;
using ShopInventory.Web.Features.UserManagement.Commands.UpdateDriverBusinessPartnerAccess;
using ShopInventory.Web.Features.UserManagement.Queries.GetDriverBusinessPartnerAccess;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public partial class DriverBusinessPartners : ComponentBase
{
    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    private readonly StringComparer _codeComparer = StringComparer.OrdinalIgnoreCase;
    private readonly HashSet<string> selectedCustomerCodes = new(StringComparer.OrdinalIgnoreCase);
    private List<BusinessPartnerDto> customers = new();
    private Dictionary<string, string> customerLabels = new(StringComparer.OrdinalIgnoreCase);
    private string customerSearchTerm = string.Empty;
    private string selectedSearchTerm = string.Empty;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool isSaving;

    private List<BusinessPartnerDto> FilteredCustomers =>
        (string.IsNullOrWhiteSpace(customerSearchTerm)
                ? customers
                : customers.Where(c =>
                    (c.CardCode?.Contains(customerSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (c.CardName?.Contains(customerSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList())
            .OrderByDescending(c => selectedCustomerCodes.Contains(c.CardCode ?? string.Empty))
            .ThenBy(c => c.CardCode)
            .ToList();

    private IEnumerable<string> FilteredSelectedCodes =>
        string.IsNullOrWhiteSpace(selectedSearchTerm)
            ? selectedCustomerCodes.OrderBy(GetCustomerDisplayName, _codeComparer)
            : selectedCustomerCodes
                .Where(code =>
                    code.Contains(selectedSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                    GetCustomerDisplayName(code).Contains(selectedSearchTerm, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetCustomerDisplayName, _codeComparer);

    private bool AllFilteredSelected =>
        FilteredCustomers.Count > 0 &&
        FilteredCustomers.All(c => selectedCustomerCodes.Contains(c.CardCode ?? string.Empty));

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync(bool preserveSelection = false)
    {
        isLoading = true;
        var preservedSelection = preserveSelection ? selectedCustomerCodes.ToList() : null;

        var result = await Mediator.Send(new GetDriverBusinessPartnerAccessQuery());
        if (result.IsError)
        {
            Snackbar.Add(result.FirstError.Description, Severity.Error);
            isLoading = false;
            return;
        }

        customers = result.Value.Customers;
        customerLabels = customers
            .Where(c => !string.IsNullOrWhiteSpace(c.CardCode))
            .GroupBy(c => c.CardCode!, _codeComparer)
            .ToDictionary(g => g.Key, g => GetBusinessPartnerDisplayName(g.First()), _codeComparer);

        selectedCustomerCodes.Clear();
        foreach (var code in preservedSelection ?? result.Value.AssignedCustomerCodes)
        {
            selectedCustomerCodes.Add(code);
        }

        isLoading = false;
    }

    private void ToggleCustomer(string cardCode, bool isSelected)
    {
        if (isSelected)
            selectedCustomerCodes.Add(cardCode);
        else
            selectedCustomerCodes.Remove(cardCode);
    }

    private void SelectAllFiltered()
    {
        foreach (var c in FilteredCustomers)
        {
            if (!string.IsNullOrWhiteSpace(c.CardCode))
                selectedCustomerCodes.Add(c.CardCode);
        }
    }

    private void DeselectAllFiltered()
    {
        foreach (var c in FilteredCustomers)
        {
            if (!string.IsNullOrWhiteSpace(c.CardCode))
                selectedCustomerCodes.Remove(c.CardCode);
        }
    }

    private void ClearAll()
    {
        selectedCustomerCodes.Clear();
        selectedSearchTerm = string.Empty;
    }

    private async Task RefreshCustomersAsync()
    {
        isRefreshing = true;
        try
        {
            var result = await Mediator.Send(new RefreshDriverBusinessPartnerAccessCommand());
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }
            await LoadAsync(preserveSelection: true);
            Snackbar.Add($"Refreshed from SAP — {result.Value} record(s) processed.", Severity.Success);
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async Task SaveAsync()
    {
        isSaving = true;
        try
        {
            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            var currentUsername = authState.User.Identity?.Name;

            var result = await Mediator.Send(
                new UpdateDriverBusinessPartnerAccessCommand(
                    selectedCustomerCodes.OrderBy(code => code).ToList(),
                    currentUsername));

            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            Snackbar.Add($"Saved. Updated {result.Value} driver account(s).", Severity.Success);
        }
        finally
        {
            isSaving = false;
        }
    }

    private string GetCustomerDisplayName(string cardCode)
        => customerLabels.TryGetValue(cardCode, out var label) ? label : cardCode;

    private static string GetBusinessPartnerDisplayName(BusinessPartnerDto bp)
    {
        var name = string.IsNullOrWhiteSpace(bp.CardName) ? bp.CardCode ?? string.Empty : bp.CardName;
        return bp.IsActive ? name : $"{name} (Inactive)";
    }
}