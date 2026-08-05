using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.ItemVolumeConversions;
using ShopInventory.Web.Features.ItemVolumeConversions.Commands.DeleteItemVolumeConversion;
using ShopInventory.Web.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;
using ShopInventory.Web.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Behaviour for the volume conversion maintenance screen.
/// </summary>
public abstract class ItemVolumeConversionsBase : ComponentBase
{
    [Inject] protected IMediator Mediator { get; set; } = default!;
    [Inject] protected IAuditService AuditService { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] protected IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] protected ILogger<ItemVolumeConversionsBase> Logger { get; set; } = default!;

    /// <summary>
    /// The list runs to several hundred rows and every one of them is a form row,
    /// so it is capped and the search is the way to the rest.
    /// </summary>
    protected const int MaxRows = 250;

    protected List<ItemVolumeConversionResult> conversions = new();
    protected List<IvxMultiSelect.Option> unmappedItemOptions = new();
    protected IReadOnlyList<string> editorItemCodes = new List<string>();

    /// <summary>
    /// Item code to catalogue description, from the product cache. The seeded factors
    /// carry a code and a number and nothing else, so most rows have no stored name
    /// and the description has to be resolved for display.
    /// </summary>
    private Dictionary<string, string> cachedItemNames = new(StringComparer.OrdinalIgnoreCase);

    protected ConversionEditorModel editor = new();
    protected ItemVolumeConversionResult? pendingDelete;

    protected string searchText = string.Empty;
    protected string statusFilter = "all";
    protected int totalCount;
    protected int activeCount;

    protected bool isLoading = true;
    protected bool isSaving;
    protected bool isEditorOpen;
    protected bool isNewFactor;
    protected string? errorMessage;
    protected string? successMessage;
    protected string? editorError;

    private string? currentUsername;

    protected List<ItemVolumeConversionResult> FilteredConversions
    {
        get
        {
            IEnumerable<ItemVolumeConversionResult> filtered = conversions;

            filtered = statusFilter switch
            {
                "active" => filtered.Where(conversion => conversion.IsActive),
                "inactive" => filtered.Where(conversion => !conversion.IsActive),
                _ => filtered
            };

            var search = searchText.Trim();
            if (!string.IsNullOrEmpty(search))
            {
                // Searches the description the row actually shows, cached or stored, so
                // typing what is on screen always finds the row.
                filtered = filtered.Where(conversion =>
                    conversion.ItemCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (DescriptionFor(conversion)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            return filtered.ToList();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        currentUsername = authState.User.Identity?.Name;

        await LoadAsync();
    }

    protected async Task LoadAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new GetItemVolumeConversionsQuery());

            result.SwitchFirst(
                value =>
                {
                    conversions = value.Conversions;
                    totalCount = value.TotalCount;
                    activeCount = value.ActiveCount;
                },
                error =>
                {
                    conversions = new List<ItemVolumeConversionResult>();
                    errorMessage = error.Description;
                });

            await LoadCachedProductsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load volume conversion factors");
            errorMessage = "Failed to load the volume conversion factors.";
        }
        finally
        {
            isLoading = false;
        }
    }

    /// <summary>
    /// One read of the product cache does two jobs: it names the rows in the table, and
    /// it fills the "new factor" picker.
    /// </summary>
    /// <remarks>
    /// The descriptions are read over the whole cache, because a factor can outlive the
    /// item it belongs to and a retired row still deserves its name. The picker is the
    /// narrower list — active products that do not already have a factor — so the common
    /// case, a product added since the last load, is a pick rather than a search through
    /// everything.
    /// </remarks>
    private async Task LoadCachedProductsAsync()
    {
        try
        {
            var mapped = conversions
                .Select(conversion => conversion.ItemCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await using var db = await DbContextFactory.CreateDbContextAsync();

            var products = await db.CachedProducts
                .AsNoTracking()
                .OrderBy(product => product.ItemCode)
                .Select(product => new { product.ItemCode, product.ItemName, product.IsActive })
                .ToListAsync();

            cachedItemNames = products
                .Where(product => !string.IsNullOrWhiteSpace(product.ItemName))
                .GroupBy(product => product.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().ItemName!.Trim(),
                    StringComparer.OrdinalIgnoreCase);

            unmappedItemOptions = products
                .Where(product => product.IsActive && !mapped.Contains(product.ItemCode))
                .Select(product => new IvxMultiSelect.Option(product.ItemCode, product.ItemCode, product.ItemName))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the cached product list for the volume conversion screen");
            cachedItemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            unmappedItemOptions = new List<IvxMultiSelect.Option>();
        }
    }

    /// <summary>
    /// The description to show for a factor: the one stored on the row if an
    /// administrator typed one, otherwise the catalogue's. Null when neither exists —
    /// a code that is not in the cache at all.
    /// </summary>
    protected string? DescriptionFor(ItemVolumeConversionResult conversion)
    {
        if (!string.IsNullOrWhiteSpace(conversion.ItemName))
        {
            return conversion.ItemName;
        }

        return cachedItemNames.TryGetValue(conversion.ItemCode, out var cached) ? cached : null;
    }

    protected void OpenNew()
    {
        isNewFactor = true;
        isEditorOpen = true;
        editorError = null;
        successMessage = null;
        editorItemCodes = new List<string>();
        editor = new ConversionEditorModel { IsActive = true };
    }

    protected void OpenEdit(ItemVolumeConversionResult conversion)
    {
        isNewFactor = false;
        isEditorOpen = true;
        editorError = null;
        successMessage = null;
        editorItemCodes = new List<string> { conversion.ItemCode };
        editor = new ConversionEditorModel
        {
            ItemCode = conversion.ItemCode,
            // Opens on the description the row is showing rather than on an empty box,
            // which also means saving an old seeded row finally stores its name.
            ItemName = DescriptionFor(conversion),
            VolumeFactor = conversion.VolumeFactor,
            Notes = conversion.Notes,
            IsActive = conversion.IsActive
        };
    }

    protected void CloseEditor()
    {
        isEditorOpen = false;
        editorError = null;
    }

    /// <summary>
    /// The picker is a multi-select the editor uses to choose one code, so the
    /// last pick wins rather than accumulating.
    /// </summary>
    protected void OnEditorItemChanged(IReadOnlyList<string> itemCodes)
    {
        var chosen = itemCodes.LastOrDefault();
        editorItemCodes = chosen is null ? new List<string>() : new List<string> { chosen };
        editor.ItemCode = chosen ?? string.Empty;

        if (chosen is not null && string.IsNullOrWhiteSpace(editor.ItemName))
        {
            editor.ItemName = unmappedItemOptions
                .FirstOrDefault(option => string.Equals(option.Value, chosen, StringComparison.OrdinalIgnoreCase))
                ?.Sub;
        }
    }

    protected async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(editor.ItemCode))
        {
            editorError = "Choose or type the item code the factor applies to.";
            return;
        }

        isSaving = true;
        editorError = null;

        try
        {
            var result = await Mediator.Send(new SaveItemVolumeConversionCommand(
                editor.ItemCode,
                editor.ItemName,
                editor.VolumeFactor,
                editor.Notes,
                editor.IsActive,
                currentUsername));

            if (result.IsError)
            {
                editorError = result.FirstError.Description;
                return;
            }

            await AuditService.LogAsync(
                AuditActions.UpdateSettings,
                "ItemVolumeConversion",
                $"{result.Value.ItemCode} = {result.Value.VolumeFactor} ({(result.Value.IsActive ? "active" : "retired")})");

            successMessage = $"Saved the volume factor for {result.Value.ItemCode}.";
            isEditorOpen = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save the volume conversion factor for {ItemCode}", editor.ItemCode);
            editorError = "Failed to save the volume conversion factor.";
        }
        finally
        {
            isSaving = false;
        }
    }

    protected void ConfirmDelete(ItemVolumeConversionResult conversion)
    {
        pendingDelete = conversion;
        successMessage = null;
    }

    protected void CancelDelete() => pendingDelete = null;

    protected async Task DeleteAsync()
    {
        if (pendingDelete is null)
        {
            return;
        }

        var itemCode = pendingDelete.ItemCode;
        isSaving = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new DeleteItemVolumeConversionCommand(itemCode));

            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            await AuditService.LogAsync(
                AuditActions.UpdateSettings,
                "ItemVolumeConversion",
                $"{itemCode} removed");

            successMessage = $"Removed the volume factor for {itemCode}.";
            pendingDelete = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to remove the volume conversion factor for {ItemCode}", itemCode);
            errorMessage = "Failed to remove the volume conversion factor.";
        }
        finally
        {
            isSaving = false;
        }
    }

    protected static string FormatDate(DateTime utc) =>
        IAuditService.ToCAT(utc).ToString("dd MMM yyyy HH:mm");

    public sealed class ConversionEditorModel
    {
        [Required]
        [MaxLength(50)]
        public string ItemCode { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? ItemName { get; set; }

        [Range(0, 100_000, ErrorMessage = "Enter the volume one sold unit represents.")]
        public decimal VolumeFactor { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
