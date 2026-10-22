using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using ShopInventory.Web.Components;
using ShopInventory.Web.Features.RouteCustomers.Commands.ImportRouteSchedule;
using ShopInventory.Web.Features.RouteCustomers.Queries.ReadRouteScheduleFile;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public partial class RouteScheduleImport
{
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>The value of the "Leave out" choice. Shop ids start at 1.</summary>
    private const int LeaveOut = 0;

    private enum View { Review, NoMatch, Matched, All }

    private static readonly View[] Views = [View.Review, View.NoMatch, View.Matched, View.All];

    [Inject] private IMediator Mediator { get; set; } = null!;

    private Guid fileKey = Guid.NewGuid();
    private string? fileName;
    private RouteScheduleWorkbookModel? workbook;
    private RouteScheduleImportResultModel? result;

    // What the office decided, carried into every check and the save.
    private readonly Dictionary<string, string?> trucks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> choices = new(StringComparer.Ordinal);

    private View selectedView = View.Review;
    private bool isReading;
    private bool isChecking;
    private bool isSaving;
    private bool confirming;
    private string? message;
    private bool messageIsError;

    private bool IsBusy => isReading || isChecking || isSaving;

    private List<string> SavableAccounts => result?.Accounts
        .Where(account => account.ScheduledAfter > 0)
        .Select(account => account.AccountCode)
        .ToList() ?? [];

    private NocturneSelectOption<string>[] TruckOptions => result?.Trucks
        .Select(truck => new NocturneSelectOption<string>(
            truck.AccountCode,
            truck.RouteName is null ? truck.AccountCode : $"{truck.AccountCode} · {truck.RouteName}")
        {
            Hint = $"{truck.ActiveShopCount:N0} shops",
        })
        .ToArray() ?? [];

    private int ReviewCount => AllOutlets().Count(outlet => InView(outlet, View.Review));

    private int NoMatchCount => AllOutlets().Count(outlet => InView(outlet, View.NoMatch));

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        ClearMessage();
        confirming = false;

        if (!file.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Upload the workbook as an .xlsx file.");
            fileKey = Guid.NewGuid();
            return;
        }

        if (file.Size > MaxFileBytes)
        {
            ShowError("That file is larger than 5 MB. The route workbook is a fraction of that — check it is the right file.");
            fileKey = Guid.NewGuid();
            return;
        }

        isReading = true;
        try
        {
            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream(MaxFileBytes))
            {
                await stream.CopyToAsync(buffer);
            }

            var read = await Mediator.Send(new ReadRouteScheduleFileQuery(buffer.ToArray()));
            if (read.IsError)
            {
                ShowError(read.FirstError.Description);
                return;
            }

            workbook = read.Value;
            fileName = file.Name;
            result = null;
            trucks.Clear();
            choices.Clear();
            selectedView = View.Review;
        }
        catch (IOException ex)
        {
            ShowError($"The file could not be read: {ex.Message}");
            return;
        }
        finally
        {
            isReading = false;
            // A fresh input, so choosing the same file again after fixing it still raises OnChange.
            fileKey = Guid.NewGuid();
        }

        await CheckAsync();
    }

    private async Task OnTruckChangedAsync(string sheet, string? account)
    {
        trucks[sheet] = account;

        // A choice of shop belongs to the truck it was made on.
        foreach (var outlet in result?.Sheets.FirstOrDefault(s => s.Name == sheet)?.Outlets ?? [])
        {
            choices.Remove(outlet.Ref);
        }

        await CheckAsync();
    }

    private async Task OnChoiceChangedAsync(RouteScheduleOutletResultModel outlet, int? choice)
    {
        if (choice is { } value)
        {
            choices[outlet.Ref] = value;
        }
        else
        {
            choices.Remove(outlet.Ref);
        }

        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (workbook is null)
        {
            return;
        }

        confirming = false;
        isChecking = true;
        try
        {
            var checkedResult = await Mediator.Send(new ImportRouteScheduleCommand(BuildRequest(validateOnly: true)));
            if (checkedResult.IsError)
            {
                ShowError(checkedResult.FirstError.Description);
                return;
            }

            result = checkedResult.Value;

            // From here on the page says which truck each sheet is, so a guess the office accepted is
            // sent back as a decision rather than guessed again.
            foreach (var sheet in result.Sheets)
            {
                trucks[sheet.Name] = sheet.AccountCode;
            }

            if (selectedView == View.Review && ReviewCount == 0)
            {
                selectedView = NoMatchCount > 0 ? View.NoMatch : View.Matched;
            }
        }
        finally
        {
            isChecking = false;
        }
    }

    private async Task SaveAsync()
    {
        if (workbook is null || SavableAccounts.Count == 0)
        {
            return;
        }

        isSaving = true;
        ClearMessage();
        try
        {
            var saved = await Mediator.Send(new ImportRouteScheduleCommand(BuildRequest(validateOnly: false)));
            if (saved.IsError)
            {
                ShowError(saved.FirstError.Description);
                return;
            }

            result = saved.Value;
            confirming = false;

            var applied = result.Accounts.Where(account => account.Applied).ToList();
            if (applied.Count == 0)
            {
                ShowError("Nothing was saved: no truck had a shop matched.");
                return;
            }

            message = "Saved. " + string.Join("; ", applied.Select(account =>
                $"{account.AccountCode}: {account.ScheduledAfter:N0} of {account.ActiveShopCount:N0} shops now have calling days")) +
                ". The van reports measure CCR against these from now on, past days included.";
            messageIsError = false;
        }
        finally
        {
            isSaving = false;
        }
    }

    private RouteScheduleImportRequestModel BuildRequest(bool validateOnly) => new()
    {
        ValidateOnly = validateOnly,
        Sheets = workbook!.Sheets
            .Where(sheet => sheet.Outlets.Count > 0)
            .Select(sheet => new RouteScheduleImportSheetModel
            {
                Name = sheet.Name,
                AccountCode = trucks.GetValueOrDefault(sheet.Name),
                Outlets = sheet.Outlets.Select(outlet =>
                {
                    var chosen = choices.TryGetValue(outlet.Ref, out var choice) ? choice : (int?)null;
                    return new RouteScheduleImportOutletModel
                    {
                        Ref = outlet.Ref,
                        Name = outlet.Name,
                        Day = outlet.Day,
                        Sequence = outlet.Sequence,
                        Excluded = chosen == LeaveOut,
                        PickedRouteCustomerId = chosen is > LeaveOut ? chosen : null,
                    };
                }).ToList(),
            })
            .ToList(),
    };

    private IEnumerable<RouteScheduleOutletResultModel> AllOutlets() =>
        result?.Sheets.SelectMany(sheet => sheet.Outlets) ?? [];

    private int CountWhere(Func<string, bool> status) => AllOutlets().Count(outlet => status(outlet.Status));

    /// <summary>
    /// Review is everything the office can act on — a close match to confirm, a name two shops carry, a
    /// near spelling, and the choices already made. No match is what nothing on the truck resembles.
    /// </summary>
    private static bool InView(RouteScheduleOutletResultModel outlet, View view) => view switch
    {
        View.Review => outlet.Status is RouteScheduleMatchStatus.Close or RouteScheduleMatchStatus.Ambiguous
                           or RouteScheduleMatchStatus.Picked or RouteScheduleMatchStatus.Excluded
                           or RouteScheduleMatchStatus.PickRejected
                       || (outlet.Status == RouteScheduleMatchStatus.Unmatched && outlet.Candidates.Count > 0),
        View.NoMatch => outlet.Status == RouteScheduleMatchStatus.NoTruck
                        || (outlet.Status == RouteScheduleMatchStatus.Unmatched && outlet.Candidates.Count == 0),
        View.Matched => outlet.Status == RouteScheduleMatchStatus.Exact,
        _ => true,
    };

    private static string ViewLabel(View view) => view switch
    {
        View.Review => "To review",
        View.NoMatch => "No match",
        View.Matched => "Matched",
        _ => "All",
    };

    private static NocturneSelectOption<int?>[] ChoicesFor(RouteScheduleOutletResultModel outlet) =>
    [
        .. outlet.Candidates.Select(candidate => new NocturneSelectOption<int?>(candidate.RouteCustomerId, candidate.Name, "good")
        {
            Hint = candidate.Code,
        }),
        new NocturneSelectOption<int?>(LeaveOut, "Leave out", "neutral"),
    ];

    private int? ChoiceOf(RouteScheduleOutletResultModel outlet) => outlet.Status switch
    {
        RouteScheduleMatchStatus.Excluded => LeaveOut,
        RouteScheduleMatchStatus.Close or RouteScheduleMatchStatus.Picked => outlet.RouteCustomerId,
        _ => choices.TryGetValue(outlet.Ref, out var choice) ? choice : null,
    };

    private static string StatusLabel(RouteScheduleOutletResultModel outlet) => outlet.Status switch
    {
        RouteScheduleMatchStatus.Exact => "Matched",
        RouteScheduleMatchStatus.Close => "Close match",
        RouteScheduleMatchStatus.Picked => "Chosen",
        RouteScheduleMatchStatus.Ambiguous => "Two shops share it",
        RouteScheduleMatchStatus.Excluded => "Left out",
        RouteScheduleMatchStatus.PickRejected => "Not on this truck",
        RouteScheduleMatchStatus.NoTruck => "No truck chosen",
        RouteScheduleMatchStatus.Unmatched when outlet.Candidates.Count > 0 => "Did you mean?",
        RouteScheduleMatchStatus.Unmatched when outlet.Sequence is null => "Area heading?",
        _ => "No match",
    };

    private static string StatusFamily(RouteScheduleOutletResultModel outlet) => outlet.Status switch
    {
        RouteScheduleMatchStatus.Exact => "ops-fam-good",
        RouteScheduleMatchStatus.Close => "ops-fam-info",
        RouteScheduleMatchStatus.Picked => "ops-fam-accent",
        RouteScheduleMatchStatus.Ambiguous or RouteScheduleMatchStatus.NoTruck => "ops-fam-warn",
        RouteScheduleMatchStatus.Excluded => "ops-fam-neutral",
        RouteScheduleMatchStatus.Unmatched when outlet.Candidates.Count > 0 => "ops-fam-warn",
        RouteScheduleMatchStatus.Unmatched when outlet.Sequence is null => "ops-fam-neutral",
        _ => "ops-fam-bad",
    };

    private void ShowError(string text)
    {
        message = text;
        messageIsError = true;
    }

    private void ClearMessage()
    {
        message = null;
        messageIsError = false;
    }
}
