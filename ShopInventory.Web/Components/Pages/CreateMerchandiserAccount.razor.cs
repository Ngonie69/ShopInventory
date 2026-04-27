using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using ShopInventory.Web.Features.UserManagement.Commands.CreateMerchandiserAccount;
using ShopInventory.Web.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;
using ShopInventory.Web.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public partial class CreateMerchandiserAccount : ComponentBase, IDisposable
{
    private const int previewCustomerCodeLimit = 6;

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private readonly CancellationTokenSource disposeCts = new();
    private readonly StringComparer customerCodeComparer = StringComparer.OrdinalIgnoreCase;
    private CreateMerchandiserAccountFormModel createForm = new();
    private EditContext createEditContext = null!;
    private List<ManagedMerchandiserAccountModel> merchandisers = new();
    private List<BusinessPartnerDto> customers = new();
    private List<WarehouseDto> warehouses = new();
    private Dictionary<string, string> customerLabels = new(StringComparer.OrdinalIgnoreCase);
    private string createCustomerSearchTerm = string.Empty;
    private string accountSearchTerm = string.Empty;
    private string editCustomerSearchTerm = string.Empty;
    private string selectedWarehouseCode = string.Empty;
    private string editingWarehouseCode = string.Empty;
    private Guid? editingMerchandiserId;
    private HashSet<string> editingAssignedCustomerCodes = new(StringComparer.OrdinalIgnoreCase);
    private bool isLoading = true;
    private bool isCreating;
    private bool isUpdating;

    private IEnumerable<BusinessPartnerDto> filteredCreateCustomers => FilterCustomers(createCustomerSearchTerm);

    private IEnumerable<BusinessPartnerDto> filteredEditCustomers => FilterCustomers(editCustomerSearchTerm);

    private IEnumerable<ManagedMerchandiserAccountModel> filteredMerchandisers =>
        merchandisers.Where(merchandiser =>
            string.IsNullOrWhiteSpace(accountSearchTerm) ||
            merchandiser.Username.Contains(accountSearchTerm, StringComparison.OrdinalIgnoreCase) ||
            (merchandiser.Email?.Contains(accountSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
            ($"{merchandiser.FirstName} {merchandiser.LastName}".Trim())
                .Contains(accountSearchTerm, StringComparison.OrdinalIgnoreCase))
        .OrderBy(merchandiser => merchandiser.Username);

    protected override async Task OnInitializedAsync()
    {
        createEditContext = new EditContext(createForm);
        await LoadViewAsync();
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
    }

    private async Task LoadViewAsync()
    {
        if (disposeCts.IsCancellationRequested)
        {
            return;
        }

        isLoading = true;

        var result = await Mediator.Send(new GetManagedMerchandiserAccountsQuery(), disposeCts.Token);
        if (result.IsError)
        {
            Snackbar.Add(result.FirstError.Description, Severity.Error);
            isLoading = false;
            return;
        }

        merchandisers = result.Value.Merchandisers;
        customers = result.Value.Customers;
        warehouses = result.Value.Warehouses;
        customerLabels = customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.CardCode))
            .GroupBy(customer => customer.CardCode!, customerCodeComparer)
            .ToDictionary(
                group => group.Key,
                group => FormatCustomerDisplayName(group.First()),
                customerCodeComparer);

        isLoading = false;
    }

    private async Task CreateAccountAsync()
    {
        isCreating = true;

        try
        {
            createForm.AssignedWarehouseCodes = string.IsNullOrWhiteSpace(selectedWarehouseCode)
                ? new List<string>()
                : new List<string> { selectedWarehouseCode };

            var result = await Mediator.Send(new CreateMerchandiserAccountCommand(createForm), disposeCts.Token);
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            Snackbar.Add($"Merchandiser account {result.Value} created successfully.", Severity.Success);
            ResetCreateForm();
            await LoadViewAsync();
        }
        finally
        {
            isCreating = false;
        }
    }

    private void ResetCreateForm()
    {
        createForm = new CreateMerchandiserAccountFormModel();
        selectedWarehouseCode = string.Empty;
        createCustomerSearchTerm = string.Empty;
        createEditContext = new EditContext(createForm);
    }

    private void ToggleCreateCustomer(string cardCode, bool isSelected)
    {
        if (isSelected)
        {
            if (!createForm.AssignedCustomerCodes.Contains(cardCode, customerCodeComparer))
            {
                createForm.AssignedCustomerCodes.Add(cardCode);
            }
        }
        else
        {
            createForm.AssignedCustomerCodes.RemoveAll(code => customerCodeComparer.Equals(code, cardCode));
        }

        createEditContext.NotifyFieldChanged(new FieldIdentifier(createForm, nameof(createForm.AssignedCustomerCodes)));
    }

    private void BeginEditAssignedCustomers(ManagedMerchandiserAccountModel merchandiser)
    {
        editingMerchandiserId = merchandiser.Id;
        editCustomerSearchTerm = string.Empty;
        editingWarehouseCode = merchandiser.AssignedWarehouseCode ?? string.Empty;
        editingAssignedCustomerCodes = merchandiser.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(customerCodeComparer);
    }

    private void CancelEditAssignedCustomers()
    {
        editingMerchandiserId = null;
        editCustomerSearchTerm = string.Empty;
        editingWarehouseCode = string.Empty;
        editingAssignedCustomerCodes.Clear();
    }

    private void ToggleEditCustomer(string cardCode, bool isSelected)
    {
        if (isSelected)
        {
            editingAssignedCustomerCodes.Add(cardCode);
            return;
        }

        editingAssignedCustomerCodes.Remove(cardCode);
    }

    private async Task SaveAssignedCustomersAsync(ManagedMerchandiserAccountModel merchandiser)
    {
        isUpdating = true;

        try
        {
            var assignedWarehouseCodes = string.IsNullOrWhiteSpace(editingWarehouseCode)
                ? new List<string>()
                : new List<string> { editingWarehouseCode.Trim() };

            var assignedCustomerCodes = editingAssignedCustomerCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(customerCodeComparer)
                .OrderBy(code => code)
                .ToList();

            var result = await Mediator.Send(
                new UpdateMerchandiserAssignedCustomersCommand(
                    merchandiser.Id,
                    merchandiser.Username,
                    assignedWarehouseCodes,
                    assignedCustomerCodes),
                disposeCts.Token);

            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            merchandiser.AssignedWarehouseCodes = assignedWarehouseCodes;
            merchandiser.AssignedCustomerCodes = assignedCustomerCodes;
            Snackbar.Add($"Assignments updated for {result.Value}.", Severity.Success);
            CancelEditAssignedCustomers();
        }
        finally
        {
            isUpdating = false;
        }
    }

    private IEnumerable<BusinessPartnerDto> FilterCustomers(string searchTerm)
        => string.IsNullOrWhiteSpace(searchTerm)
            ? customers.OrderBy(customer => customer.CardCode).Take(150)
            : customers.Where(customer =>
                    (customer.CardCode?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (customer.CardName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(customer => customer.CardCode)
                .Take(150);

    private string GetCustomerDisplayName(string customerCode)
        => customerLabels.TryGetValue(customerCode, out var label)
            ? label
            : customerCode;

    private static string FormatCustomerDisplayName(BusinessPartnerDto customer)
    {
        var displayName = customer.DisplayName;
        return customer.IsActive ? displayName : $"{displayName} (Inactive)";
    }

    private IEnumerable<string> GetPreviewCustomerCodes(ManagedMerchandiserAccountModel merchandiser)
        => merchandiser.AssignedCustomerCodes.Take(previewCustomerCodeLimit);

    private static string GetMerchandiserSubtitle(ManagedMerchandiserAccountModel merchandiser)
    {
        var fullName = $"{merchandiser.FirstName} {merchandiser.LastName}".Trim();

        if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(merchandiser.Email))
        {
            return $"{fullName} • {merchandiser.Email}";
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        return merchandiser.Email ?? "No profile details available";
    }
}