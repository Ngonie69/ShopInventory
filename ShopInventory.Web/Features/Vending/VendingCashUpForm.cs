using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// The capture form's state for one cashier's day, and the arithmetic it previews while it is typed.
/// </summary>
/// <remarks>
/// The preview follows the API's rule exactly — cash variance is counted less float and cash sales,
/// a tender's is declared less expected, both per currency and rounded to the cent — but it is only a
/// preview: the saved figures and the approval are the API's. A currency nobody has typed a count for
/// has no variance, never one of minus the day's sales.
/// </remarks>
public sealed class VendingCashUpForm
{
    /// <summary>The notes the depots handle, largest first. Coins are counted as one amount.</summary>
    public static readonly IReadOnlyDictionary<string, decimal[]> NotesByCurrency = new Dictionary<string, decimal[]>
    {
        ["USD"] = [100m, 50m, 20m, 10m, 5m, 2m, 1m],
        ["ZIG"] = [200m, 100m, 50m, 20m, 10m, 5m, 2m, 1m]
    };

    /// <summary>The coin line travels as this many hundredths, so the API's notes-add-up rule holds for it too.</summary>
    public const decimal CoinValue = 0.01m;

    public static readonly string[] NonCashTenders = ["Swipe", "Ecocash", "Innbucks"];

    public List<CurrencyForm> Currencies { get; } = new();

    public List<TenderForm> Tenders { get; } = new();

    public string? VarianceReason { get; set; }

    public string? Note { get; set; }

    public static VendingCashUpForm From(VendingCashUpSheetModel sheet)
    {
        var form = new VendingCashUpForm
        {
            VarianceReason = sheet.VarianceReason,
            Note = sheet.Note
        };
        var counted = sheet.CashUpId is not null;

        foreach (var line in sheet.Currencies)
        {
            var currency = new CurrencyForm(line.Currency, line.ExpectedCash, line.ExpectedCashSales)
            {
                OpeningFloat = counted ? line.OpeningFloat : null,
                Banked = counted ? line.Banked : null,
                DepositReference = line.DepositReference
            };

            var notes = line.Denominations.Where(note => note.Value != CoinValue).ToList();
            var coins = line.Denominations.FirstOrDefault(note => note.Value == CoinValue);
            if (line.Denominations.Count > 0)
            {
                currency.ByNotes = true;
                foreach (var note in notes)
                {
                    currency.Notes[note.Value] = note.Quantity;
                }

                currency.Coins = coins is null ? null : coins.Quantity * CoinValue;
            }
            else if (counted)
            {
                currency.Total = line.CountedCash;
            }

            form.Currencies.Add(currency);
        }

        foreach (var code in form.Currencies.Select(currency => currency.Currency))
        {
            foreach (var tender in NonCashTenders)
            {
                var saved = sheet.Tenders.FirstOrDefault(line =>
                    line.Currency == code && string.Equals(line.Tender, tender, StringComparison.OrdinalIgnoreCase));
                form.Tenders.Add(new TenderForm(code, tender, saved?.Expected ?? 0m, saved?.ExpectedSales ?? 0)
                {
                    Declared = counted ? saved?.Declared ?? 0m : null
                });
            }
        }

        // A tender the sales carry that the form does not offer (an old till's "Transfer") is still
        // shown, so its expected figure is visible; it cannot be declared, and its whole amount is the
        // variance until the API is taught the tender.
        foreach (var line in sheet.Tenders.Where(line => !form.Tenders.Any(tender =>
                     tender.Currency == line.Currency && string.Equals(tender.Tender, line.Tender, StringComparison.OrdinalIgnoreCase))))
        {
            form.Tenders.Add(new TenderForm(line.Currency, line.Tender, line.Expected, line.ExpectedSales)
            {
                Declared = line.Declared,
                Declarable = false
            });
        }

        return form;
    }

    /// <summary>Whether every currency has a count typed in. The API refuses a currency with none.</summary>
    public bool IsComplete => Currencies.All(currency => currency.Counted is not null);

    /// <summary>Whether anything typed disagrees with the sales. False while a count is missing.</summary>
    public bool HasVariance =>
        IsComplete
        && (Currencies.Any(currency => currency.Variance is not 0m)
            || Tenders.Any(tender => tender.Variance is not 0m));

    public bool NeedsReason => HasVariance && string.IsNullOrWhiteSpace(VarianceReason);

    public SaveVendingCashUpRequestModel ToRequest(VendingCashUpSheetModel sheet) => new()
    {
        CashierUserId = sheet.CashierUserId,
        WarehouseCode = sheet.WarehouseCode,
        BusinessDate = sheet.BusinessDate.Date,
        ExpectedRevision = sheet.Revision,
        Currencies = Currencies.Select(currency => new SaveVendingCashUpCurrencyModel
        {
            Currency = currency.Currency,
            OpeningFloat = currency.OpeningFloat ?? 0m,
            CountedCash = currency.ByNotes ? null : currency.Total,
            Banked = currency.Banked ?? 0m,
            DepositReference = string.IsNullOrWhiteSpace(currency.DepositReference) ? null : currency.DepositReference.Trim(),
            Denominations = currency.ByNotes ? currency.DenominationLines().ToList() : null
        }).ToList(),
        Tenders = Tenders
            .Where(tender => tender.Declarable && (tender.Declared ?? 0m) != 0m)
            .Select(tender => new SaveVendingCashUpTenderModel
            {
                Currency = tender.Currency,
                Tender = tender.Tender,
                Declared = tender.Declared ?? 0m
            }).ToList(),
        VarianceReason = string.IsNullOrWhiteSpace(VarianceReason) ? null : VarianceReason.Trim(),
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()
    };

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public sealed class CurrencyForm(string currency, decimal expectedCash, int expectedSales)
    {
        public string Currency { get; } = currency;
        public decimal ExpectedCash { get; } = expectedCash;
        public int ExpectedSales { get; } = expectedSales;
        public decimal? OpeningFloat { get; set; }
        public bool ByNotes { get; set; }
        public Dictionary<decimal, int?> Notes { get; } = new();
        public decimal? Coins { get; set; }
        public decimal? Total { get; set; }
        public decimal? Banked { get; set; }
        public string? DepositReference { get; set; }

        public IEnumerable<decimal> NoteValues =>
            NotesByCurrency.TryGetValue(Currency, out var values) ? values : [];

        /// <summary>The drawer as counted, float included; null until something is typed.</summary>
        public decimal? Counted
        {
            get
            {
                if (!ByNotes)
                {
                    return Total is { } total ? Round(total) : null;
                }

                var anyTyped = Notes.Values.Any(quantity => quantity is not null) || Coins is not null;
                return anyTyped ? Round(DenominationLines().Sum(line => line.Value * line.Quantity)) : null;
            }
        }

        public decimal ExpectedInDrawer => Round((OpeningFloat ?? 0m) + ExpectedCash);

        public decimal? Variance => Counted is { } counted ? Round(counted - ExpectedInDrawer) : null;

        public decimal? ToBank => Counted is { } counted ? Math.Max(0m, Round(counted - (OpeningFloat ?? 0m))) : null;

        public IEnumerable<VendingCashUpDenominationModel> DenominationLines()
        {
            foreach (var (value, quantity) in Notes.Where(pair => pair.Value is > 0).OrderByDescending(pair => pair.Key))
            {
                yield return new VendingCashUpDenominationModel { Value = value, Quantity = quantity!.Value };
            }

            if (Coins is > 0m && Round(Coins.Value / CoinValue) is var cents and > 0)
            {
                yield return new VendingCashUpDenominationModel { Value = CoinValue, Quantity = (int)cents };
            }
        }
    }

    public sealed class TenderForm(string currency, string tender, decimal expected, int expectedSales)
    {
        public string Currency { get; } = currency;
        public string Tender { get; } = tender;
        public decimal Expected { get; } = expected;
        public int ExpectedSales { get; } = expectedSales;
        public decimal? Declared { get; set; }
        public bool Declarable { get; init; } = true;

        /// <summary>As the API reads it: a tender left blank was declared at nothing.</summary>
        public decimal Variance => Round((Declared ?? 0m) - Expected);
    }
}
