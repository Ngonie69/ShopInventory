using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Checks every <c>$select</c> the SAP client sends against the Service Layer's own
/// <c>$metadata</c>.
/// </summary>
/// <remarks>
/// An unknown name in <c>$select</c> is not ignored — SAP returns 400 and the endpoint stops
/// working. Hand-checking 49 URLs once is not a control; this is, and it also catches the reverse
/// mistake of adding a property to a model without widening the select, which would leave the
/// property silently null.
///
/// It is a necessary check, not a sufficient one, and the difference matters. <c>Document</c> is a
/// single EDM type shared by every marketing document, so its property list is the union across all
/// of them, while SAP validates <c>$select</c> against the entity set at runtime. A name can
/// therefore be present here and still be rejected: <c>DocTotal</c> on <c>PurchaseRequests</c> and
/// <c>BaseEntry</c> on <c>CreditNotes</c> both passed this test and were refused by SAP. UDFs cut
/// the other way — they are absent from the metadata of a company that does not define them, so
/// this cannot speak to them at all.
///
/// <c>ShopInventory.IntegrationTests</c> is what actually settles both. Keep this because it is
/// free, offline and catches typos in the edit that introduces them.
/// </remarks>
public class SapSelectClauseTests
{
    private static readonly Lazy<XDocument> Metadata = new(() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "reference", "sap-service-layer-metadata.xml")));

    /// <summary>Entity sets whose EDM type each select constant is sent against.</summary>
    public static TheoryData<string, string> SelectConstants() => new()
    {
        { "PurchaseOrderSelect", "Document" },
        { "PurchaseRequestSelect", "Document" },
        { "PurchaseQuotationSelect", "Document" },
        { "GoodsReceiptPurchaseOrderSelect", "Document" },
        { "PurchaseInvoiceSelect", "Document" },
        { "CreditNoteSelect", "Document" },
        { "QuotationSelect", "Document" },
        { "StockTransferSelect", "StockTransfer" },
        { "InventoryTransferRequestSelect", "StockTransfer" },
        { "IncomingPaymentSelect", "Payment" },
        { "BusinessPartnerSelectFields", "BusinessPartner" },
        { "BusinessPartnerCreditSelect", "BusinessPartner" },
        { "ItemSelect", "Item" },
    };

    [Theory]
    [MemberData(nameof(SelectConstants))]
    public void Every_selected_field_exists_on_its_sap_type(string constantName, string entityTypeName)
    {
        var fields = FieldsOf(ReadConstant(constantName));
        var known = PropertiesOf(entityTypeName);

        var unknown = fields.Where(field => !known.Contains(field)).ToList();

        Assert.True(
            unknown.Count == 0,
            $"{constantName} selects {string.Join(", ", unknown)}, which {entityTypeName} does not define. "
            + "SAP answers 400 for an unknown $select field.");
    }

    [Theory]
    [MemberData(nameof(SelectConstants))]
    public void Select_lists_have_no_duplicates(string constantName, string entityTypeName)
    {
        _ = entityTypeName;
        var fields = FieldsOf(ReadConstant(constantName));

        Assert.Equal(fields.Distinct(StringComparer.Ordinal).Count(), fields.Count);
    }

    [Fact]
    public void Document_type_is_shared_by_every_marketing_document_set()
    {
        // The reason one field list is safe across purchase orders, credit notes and quotations.
        var sets = new[]
        {
            "Orders", "Invoices", "Quotations", "CreditNotes",
            "PurchaseOrders", "PurchaseInvoices", "PurchaseQuotations", "PurchaseDeliveryNotes"
        };

        foreach (var set in sets)
        {
            Assert.Equal("SAPB1.Document", EntityTypeOf(set));
        }
    }

    private static string ReadConstant(string name)
    {
        var field = typeof(SAPServiceLayerClient).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.GetField)
            ?? throw new InvalidOperationException($"No constant named {name}.");

        return (string)field.GetRawConstantValue()!;
    }

    private static List<string> FieldsOf(string selectClause)
    {
        var value = Regex.Match(selectClause, @"\$select=([^&]+)").Groups[1].Value;
        return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static HashSet<string> PropertiesOf(string entityTypeName)
    {
        var type = Metadata.Value.Descendants()
            .Single(element => element.Name.LocalName == "EntityType"
                && (string?)element.Attribute("Name") == entityTypeName);

        return [.. type.Elements()
            .Where(element => element.Name.LocalName is "Property" or "NavigationProperty")
            .Select(element => (string)element.Attribute("Name")!)];
    }

    private static string EntityTypeOf(string entitySetName) =>
        (string)Metadata.Value.Descendants()
            .Single(element => element.Name.LocalName == "EntitySet"
                && (string?)element.Attribute("Name") == entitySetName)
            .Attribute("EntityType")!;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
