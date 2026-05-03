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

    private readonly StringComparer customerCodeComparer = StringComparer.OrdinalIgnoreCase;
    private readonly HashSet<string> selectedCustomerCodes = new(StringComparer.OrdinalIgnoreCase);
    private List<BusinessPartnerDto> customers = new();
    private Dictionary<string, string> customerLabels = new(StringComparer.OrdinalIgnoreCase);
    private string customerSearchTerm = string.Empty;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool isSaving;

    private IEnumerable<BusinessPartnerDto> FilteredCustomers =>
        (string.IsNullOrWhiteSpace(customerSearchTerm)
                ? customers.AsEnumerable()
                : customers.Where(customer =>
                    (customer.CardCode?.Contains(customerSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (customer.CardName?.Contains(customerSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(customer => selectedCustomerCodes.Contains(customer.CardCode ?? string.Empty))
            .ThenBy(customer => customer.CardCode);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync(bool preserveSelection = false)
    {
        isLoading = true;
        var preservedSelection = preserveSelection
            ? selectedCustomerCodes.ToList()
            : null;

        var result = await Mediator.Send(new GetDriverBusinessPartnerAccessQuery());
        if (result.IsError)
        {
            Snackbar.Add(result.FirstError.Description, Severity.Error);
            isLoading = false;
            return;
        }

        customers = result.Value.Customers;
        customerLabels = customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.CardCode))
            .GroupBy(customer => customer.CardCode!, customerCodeComparer)
            .ToDictionary(group => group.Key, group => GetBusinessPartnerDisplayName(group.First()), customerCodeComparer);

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
        {
            selectedCustomerCodes.Add(cardCode);
            return;
        }

        selectedCustomerCodes.Remove(cardCode);
    }

    private IEnumerable<string> GetSelectedCustomerCodes()
        => selectedCustomerCodes.OrderBy(GetCustomerDisplayName, StringComparer.OrdinalIgnoreCase);

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
            Snackbar.Add($"Refreshed local driver shop list from SAP. {result.Value} business partner record(s) processed.", Severity.Success);
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

            Snackbar.Add($"Driver shop access saved. Updated {result.Value} driver account(s).", Severity.Success);
        }
        finally
        {
            isSaving = false;
        }
    }

    private string GetCustomerDisplayName(string cardCode)
        => customerLabels.TryGetValue(cardCode, out var label)
            ? label
            : cardCode;

    private static string GetBusinessPartnerDisplayName(BusinessPartnerDto businessPartner)
    {
        var displayName = string.IsNullOrWhiteSpace(businessPartner.CardName)
            ? businessPartner.CardCode ?? string.Empty
            : businessPartner.CardName;

        return businessPartner.IsActive ? displayName : $"{displayName} (Inactive)";
    }
}