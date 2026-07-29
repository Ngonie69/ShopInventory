using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxStructuredPayloadParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static bool HasItems(object? payload)
    {
        if (payload is null)
        {
            return false;
        }

        if (payload is string stringPayload)
        {
            return !string.IsNullOrWhiteSpace(stringPayload);
        }

        if (payload is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Array => jsonElement.GetArrayLength() > 0,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(jsonElement.GetString()),
                _ => false
            };
        }

        return payload is IEnumerable<RevmaxRequestItem> items && items.Any();
    }

    internal static ErrorOr<List<RevmaxRequestItem>> NormalizeItems(object? payload, decimal configuredVatRate)
    {
        if (payload is null)
        {
            return Errors.Revmax.InvalidItems;
        }

        if (TryReadItemsFromPayload(payload, out var items, out var rawString))
        {
            return ValidateAndNormalizeItems(items, configuredVatRate);
        }

        if (!string.IsNullOrWhiteSpace(rawString))
        {
            return NormalizeItemsFromString(rawString, configuredVatRate);
        }

        return Error.Validation("Revmax.InvalidItems", "ItemsXml must be a structured item array or a legacy XML string.");
    }

    internal static List<RevmaxRequestCurrency> NormalizeCurrencies(
        object? payload,
        string? defaultCurrency,
        decimal defaultAmount)
    {
        if (TryReadCurrenciesFromPayload(payload, out var currencies, out var rawString))
        {
            return ValidateAndNormalizeCurrencies(currencies, defaultCurrency, defaultAmount);
        }

        if (!string.IsNullOrWhiteSpace(rawString))
        {
            var normalizedFromString = TryNormalizeCurrenciesFromString(rawString, defaultCurrency, defaultAmount);
            if (normalizedFromString.Count > 0)
            {
                return normalizedFromString;
            }
        }

        return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
    }

    internal static string SerializeItemsXml(IEnumerable<RevmaxRequestItem> items)
    {
        var root = new XElement("items");

        foreach (var item in items)
        {
            root.Add(new XElement("item",
                new XElement("HH", item.HH ?? string.Empty),
                new XElement("ITEMCODE", item.ItemCode ?? string.Empty),
                new XElement("ITEMNAME1", item.ItemName1 ?? string.Empty),
                new XElement("ITEMNAME2", item.ItemName2 ?? item.ItemName1 ?? string.Empty),
                new XElement("QTY", item.Qty ?? string.Empty),
                new XElement("PRICE", item.Price ?? string.Empty),
                new XElement("AMT", item.Amt ?? string.Empty),
                new XElement("TAX", item.Tax ?? string.Empty),
                new XElement("TAXR", item.TaxR ?? string.Empty)));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal static string SerializeCurrenciesXml(IEnumerable<RevmaxRequestCurrency> currencies)
    {
        var root = new XElement("currencies");

        foreach (var currency in currencies)
        {
            root.Add(new XElement("currency",
                new XElement("Name", currency.Name ?? string.Empty),
                new XElement("Amount", currency.Amount ?? string.Empty),
                new XElement("Rate", currency.Rate ?? string.Empty)));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal static TransactMRequest BuildUpstreamRequest(
        TransactMRequest request,
        IReadOnlyList<RevmaxRequestItem> items,
        IReadOnlyList<RevmaxRequestCurrency> currencies)
        => new()
        {
            Currency = request.Currency,
            BranchName = request.BranchName,
            InvoiceNumber = request.InvoiceNumber,
            OriginalInvoiceNumber = request.OriginalInvoiceNumber,
            CustomerName = request.CustomerName,
            CustomerVatNumber = request.CustomerVatNumber,
            CustomerAddress = request.CustomerAddress,
            CustomerTelephone = request.CustomerTelephone,
            CustomerEmail = request.CustomerEmail,
            CustomerBPN = request.CustomerBPN,
            InvoiceAmount = request.InvoiceAmount,
            InvoiceTaxAmount = request.InvoiceTaxAmount,
            Istatus = request.Istatus,
            Cashier = request.Cashier,
            InvoiceComment = request.InvoiceComment,
            ItemsXml = SerializeItemsXml(items),
            CurrenciesXml = SerializeCurrenciesXml(currencies)
        };

    internal static TransactMExtRequest BuildUpstreamRequest(
        TransactMExtRequest request,
        IReadOnlyList<RevmaxRequestItem> items,
        IReadOnlyList<RevmaxRequestCurrency> currencies)
        => new()
        {
            Currency = request.Currency,
            BranchName = request.BranchName,
            InvoiceNumber = request.InvoiceNumber,
            OriginalInvoiceNumber = request.OriginalInvoiceNumber,
            CustomerName = request.CustomerName,
            CustomerVatNumber = request.CustomerVatNumber,
            CustomerAddress = request.CustomerAddress,
            CustomerTelephone = request.CustomerTelephone,
            CustomerEmail = request.CustomerEmail,
            CustomerBPN = request.CustomerBPN,
            InvoiceAmount = request.InvoiceAmount,
            InvoiceTaxAmount = request.InvoiceTaxAmount,
            Istatus = request.Istatus,
            Cashier = request.Cashier,
            InvoiceComment = request.InvoiceComment,
            ItemsXml = SerializeItemsXml(items),
            CurrenciesXml = SerializeCurrenciesXml(currencies),
            refDeviceId = request.refDeviceId,
            refReceiptGlobalNo = request.refReceiptGlobalNo,
            refFiscalDayNo = request.refFiscalDayNo
        };

    private static ErrorOr<List<RevmaxRequestItem>> NormalizeItemsFromString(string rawString, decimal configuredVatRate)
    {
        var trimmed = rawString.Trim();
        if (trimmed.Length == 0)
        {
            return Errors.Revmax.InvalidItems;
        }

        if (trimmed.StartsWith('<'))
        {
            try
            {
                var doc = XDocument.Parse(trimmed);
                var items = doc.Descendants("item")
                    .Select((item, index) => new RevmaxRequestItem
                    {
                        HH = GetElementValue(item, "HH") ?? index.ToString(CultureInfo.InvariantCulture),
                        ItemCode = GetElementValue(item, "ITEMCODE", "ItemCode"),
                        ItemName1 = GetElementValue(item, "ITEMNAME1", "ItemName1"),
                        ItemName2 = GetElementValue(item, "ITEMNAME2", "ItemName2"),
                        Qty = GetElementValue(item, "QTY", "Qty"),
                        Price = GetElementValue(item, "PRICE", "Price"),
                        Amt = GetElementValue(item, "AMT", "Amt"),
                        Tax = GetElementValue(item, "TAX", "Tax"),
                        TaxR = GetElementValue(item, "TAXR", "TaxR")
                    })
                    .ToList();

                return ValidateAndNormalizeItems(items, configuredVatRate);
            }
            catch (System.Xml.XmlException ex)
            {
                return Error.Validation("Revmax.InvalidItemsXml", $"Invalid ItemsXml format: {ex.Message}");
            }
        }

        if (trimmed.StartsWith('['))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<RevmaxRequestItem>>(trimmed, JsonOptions) ?? new List<RevmaxRequestItem>();
                return ValidateAndNormalizeItems(items, configuredVatRate);
            }
            catch (JsonException ex)
            {
                return Error.Validation("Revmax.InvalidItems", $"Invalid ItemsXml JSON format: {ex.Message}");
            }
        }

        return Error.Validation("Revmax.InvalidItems", "ItemsXml must be a structured item array or a legacy XML string.");
    }

    private static ErrorOr<List<RevmaxRequestItem>> ValidateAndNormalizeItems(
        IReadOnlyList<RevmaxRequestItem> items,
        decimal configuredVatRate)
    {
        if (items.Count == 0)
        {
            return Error.Validation("Revmax.NoItems", "At least one item is required in ItemsXml");
        }

        var errorMessages = new List<string>();
        var normalizedItems = new List<RevmaxRequestItem>(items.Count);
        var defaultTaxRate = NormalizeTaxRate(configuredVatRate);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index] ?? new RevmaxRequestItem();
            var lineNumber = index + 1;
            var itemCode = item.ItemCode?.Trim();

            if (string.IsNullOrWhiteSpace(itemCode))
            {
                errorMessages.Add($"Line {lineNumber}: ItemCode is required");
            }

            if (!TryParseDecimal(item.Qty, out var qty) || qty <= 0m)
            {
                errorMessages.Add($"Line {lineNumber}: Qty must be > 0");
            }

            if (!TryParseDecimal(item.Price, out var price) || price < 0m)
            {
                errorMessages.Add($"Line {lineNumber}: Price must be >= 0");
            }

            decimal amount;
            if (string.IsNullOrWhiteSpace(item.Amt))
            {
                amount = qty * price;
            }
            else if (!TryParseDecimal(item.Amt, out amount) || amount < 0m)
            {
                errorMessages.Add($"Line {lineNumber}: Amt must be >= 0");
                amount = 0m;
            }

            var tax = string.IsNullOrWhiteSpace(item.Tax)
                ? "0"
                : item.Tax.Trim();

            decimal taxRate;
            if (string.IsNullOrWhiteSpace(item.TaxR))
            {
                taxRate = IsVatExempt(tax) ? 0m : defaultTaxRate;
            }
            else if (!TryParseDecimal(item.TaxR, out taxRate))
            {
                errorMessages.Add($"Line {lineNumber}: TaxR must be a valid number");
                taxRate = 0m;
            }
            else
            {
                taxRate = NormalizeTaxRate(taxRate);
            }

            normalizedItems.Add(new RevmaxRequestItem
            {
                HH = string.IsNullOrWhiteSpace(item.HH)
                    ? index.ToString(CultureInfo.InvariantCulture)
                    : item.HH.Trim(),
                ItemCode = itemCode ?? string.Empty,
                ItemName1 = item.ItemName1 ?? string.Empty,
                ItemName2 = string.IsNullOrWhiteSpace(item.ItemName2)
                    ? item.ItemName1 ?? string.Empty
                    : item.ItemName2,
                Qty = qty.ToString(CultureInfo.InvariantCulture),
                Price = price.ToString(CultureInfo.InvariantCulture),
                Amt = amount.ToString(CultureInfo.InvariantCulture),
                Tax = tax,
                TaxR = taxRate.ToString(CultureInfo.InvariantCulture)
            });
        }

        if (errorMessages.Count > 0)
        {
            return Error.Validation("Revmax.InvalidItems", string.Join("; ", errorMessages));
        }

        return normalizedItems;
    }

    private static List<RevmaxRequestCurrency> ValidateAndNormalizeCurrencies(
        IReadOnlyList<RevmaxRequestCurrency> currencies,
        string? defaultCurrency,
        decimal defaultAmount)
    {
        if (currencies.Count == 0)
        {
            return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
        }

        var normalizedCurrencies = new List<RevmaxRequestCurrency>(currencies.Count);
        foreach (var currency in currencies)
        {
            var name = string.IsNullOrWhiteSpace(currency?.Name)
                ? NormalizeCurrency(defaultCurrency)
                : currency!.Name!.Trim();
            var amount = TryParseDecimal(currency?.Amount, out var parsedAmount) && parsedAmount >= 0m
                ? parsedAmount
                : Math.Abs(defaultAmount);
            var rate = TryParseDecimal(currency?.Rate, out var parsedRate) && parsedRate > 0m
                ? parsedRate
                : 1m;

            normalizedCurrencies.Add(new RevmaxRequestCurrency
            {
                Name = name,
                Amount = amount.ToString(CultureInfo.InvariantCulture),
                Rate = rate.ToString(CultureInfo.InvariantCulture)
            });
        }

        return normalizedCurrencies;
    }

    private static List<RevmaxRequestCurrency> TryNormalizeCurrenciesFromString(
        string rawString,
        string? defaultCurrency,
        decimal defaultAmount)
    {
        var trimmed = rawString.Trim();
        if (trimmed.Length == 0)
        {
            return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
        }

        if (trimmed.StartsWith('<'))
        {
            try
            {
                var doc = XDocument.Parse(trimmed);
                var currencies = doc.Descendants("currency")
                    .Select(currency => new RevmaxRequestCurrency
                    {
                        Name = GetElementValue(currency, "Name"),
                        Amount = GetElementValue(currency, "Amount"),
                        Rate = GetElementValue(currency, "Rate")
                    })
                    .ToList();

                return ValidateAndNormalizeCurrencies(currencies, defaultCurrency, defaultAmount);
            }
            catch (System.Xml.XmlException)
            {
                return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
            }
        }

        if (trimmed.StartsWith('['))
        {
            try
            {
                var currencies = JsonSerializer.Deserialize<List<RevmaxRequestCurrency>>(trimmed, JsonOptions) ?? new List<RevmaxRequestCurrency>();
                return ValidateAndNormalizeCurrencies(currencies, defaultCurrency, defaultAmount);
            }
            catch (JsonException)
            {
                return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
            }
        }

        return BuildDefaultCurrencies(defaultCurrency, defaultAmount);
    }

    private static List<RevmaxRequestCurrency> BuildDefaultCurrencies(string? defaultCurrency, decimal defaultAmount)
        => new()
        {
            new RevmaxRequestCurrency
            {
                Name = NormalizeCurrency(defaultCurrency),
                Amount = Math.Abs(defaultAmount).ToString(CultureInfo.InvariantCulture),
                Rate = "1"
            }
        };

    private static bool TryReadItemsFromPayload(object payload, out List<RevmaxRequestItem> items, out string? rawString)
    {
        rawString = null;

        switch (payload)
        {
            case IEnumerable<RevmaxRequestItem> itemEnumerable:
                items = itemEnumerable.ToList();
                return true;

            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Array:
                items = JsonSerializer.Deserialize<List<RevmaxRequestItem>>(jsonElement.GetRawText(), JsonOptions) ?? new List<RevmaxRequestItem>();
                return true;

            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String:
                items = new List<RevmaxRequestItem>();
                rawString = jsonElement.GetString();
                return false;

            case string stringPayload:
                items = new List<RevmaxRequestItem>();
                rawString = stringPayload;
                return false;

            default:
                items = new List<RevmaxRequestItem>();
                return false;
        }
    }

    private static bool TryReadCurrenciesFromPayload(object? payload, out List<RevmaxRequestCurrency> currencies, out string? rawString)
    {
        rawString = null;

        switch (payload)
        {
            case IEnumerable<RevmaxRequestCurrency> currencyEnumerable:
                currencies = currencyEnumerable.ToList();
                return true;

            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Array:
                currencies = JsonSerializer.Deserialize<List<RevmaxRequestCurrency>>(jsonElement.GetRawText(), JsonOptions) ?? new List<RevmaxRequestCurrency>();
                return true;

            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String:
                currencies = new List<RevmaxRequestCurrency>();
                rawString = jsonElement.GetString();
                return false;

            case string stringPayload:
                currencies = new List<RevmaxRequestCurrency>();
                rawString = stringPayload;
                return false;

            default:
                currencies = new List<RevmaxRequestCurrency>();
                return false;
        }
    }

    private static string? GetElementValue(XElement element, params string[] elementNames)
    {
        foreach (var elementName in elementNames)
        {
            var child = element.Element(elementName);
            if (child is not null)
            {
                return child.Value;
            }
        }

        return null;
    }

    private static decimal NormalizeTaxRate(decimal value)
        => value > 1m ? value / 100m : value;

    private static bool IsVatExempt(string tax)
        => string.Equals(tax, "0", StringComparison.OrdinalIgnoreCase)
           || string.Equals(tax, "exempt", StringComparison.OrdinalIgnoreCase)
           || string.Equals(tax, "E", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDecimal(string? value, out decimal parsed)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim();
}