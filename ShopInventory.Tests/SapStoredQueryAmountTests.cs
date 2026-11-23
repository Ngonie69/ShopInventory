using System.Text.RegularExpressions;
using ShopInventory.Common.Sap;

namespace ShopInventory.Tests;

/// <summary>
/// Fails when a stored SAP query in the API's source selects a decimal column as a number.
/// </summary>
/// <remarks>
/// <para>
/// A Service Layer worker writing a decimal into a <c>SQLQueries('code')/List</c> answer can stall for
/// good; text cannot (<see cref="SapSqlAmount"/>). Every statement the API runs is created by the API
/// from a string in its own source, so the source is what this reads: each C# string literal holding a
/// <c>SELECT … FROM</c> over a SAP table, with its <see cref="SapSqlAmount.Column(string, string)"/>
/// and <see cref="SapSqlAmount.Sum"/> holes filled in by the real helper.
/// </para>
/// <para>
/// A selected column passes only if it is known to be text, a whole number or a date
/// (<see cref="NotDecimal"/>). One nobody has classified fails, so a new decimal cannot arrive by being
/// unlisted. A total fails too: <c>SUM(x)</c> is as much a decimal as <c>x</c>, and the Service Layer
/// takes <c>LEFT(SUM(x), 40)</c> (tried on 23 Nov 2026). The <c>WHERE</c> is not looked at: comparing a
/// decimal there writes nothing.
/// </para>
/// </remarks>
public class SapStoredQueryAmountTests
{
    /// <summary>The columns the stored queries select that SAP holds as a decimal.</summary>
    private static readonly HashSet<string> Decimal = new(StringComparer.Ordinal)
    {
        "Quantity", "OnHand", "IsCommited", "OnOrder", "Price", "Factor", "Rate",
        "LineTotal", "TotalFrgn", "GrssProfit", "GrssProfFC", "DocTotal", "VatSum", "PaidToDate",
        "Debit", "Credit", "FCDebit", "FCCredit", "BalDueDeb", "BalDueCred",
    };

    /// <summary>
    /// The columns the stored queries select that are text, a whole number or a date. Add a column here
    /// only after checking its type in SAP; a decimal goes through <see cref="SapSqlAmount"/> instead.
    /// </summary>
    private static readonly HashSet<string> NotDecimal = new(StringComparer.Ordinal)
    {
        // Items and stock
        "ItemCode", "ItemName", "FrgnName", "CodeBars", "InvntryUom", "SalUnitMsr", "ItmsGrpCod",
        "U_VanSale", "U_SalesItem", "U_ItemGroup", "U_PackagingCode", "U_PackagingCodeLabels", "U_PackagingCodeLids",
        "WhsCode", "UomCode", "UomEntry",
        // Batches and serials
        "AbsEntry", "DistNumber", "Status", "ExpDate", "MnfDate", "InDate", "Notes", "SysNumber", "MnfSerial",
        // Price lists and rates
        "ListNum", "ListName", "BASE_NUM", "RoundSys", "ValidFor", "PriceList", "Currency", "RateDate",
        // Documents and their lines
        "DocEntry", "DocNum", "DocDate", "DocDueDate", "DocCur", "DocStatus", "CANCELED", "CardCode", "CardName",
        "LineNum", "Dscription", "LineStatus", "BaseEntry", "BaseLine", "BaseRef", "U_Reasons",
        // Journal entries
        "RefDate", "DueDate", "Number", "TransType", "Memo", "CreatedBy", "Line_ID", "ShortName", "ContraAct",
        "LineMemo", "FCCurrency",
    };

    /// <summary>
    /// Pieces of a select list held in a variable rather than in the statement's own literal, and what
    /// they hold. Checked like any other part of the list.
    /// </summary>
    private static readonly Dictionary<string, string> SelectListFragments = new(StringComparer.Ordinal)
    {
        // GetStockQuantitiesInWarehouseAsync: the packaging codes, when SAP:UseCustomFields is on.
        ["customFieldsSql"] =
            """, T0."U_PackagingCode" as "PackagingCode", T0."U_PackagingCodeLabels" as "PackagingCodeLabels", T0."U_PackagingCodeLids" as "PackagingCodeLids" """,
    };

    [Fact]
    public void No_stored_query_selects_a_decimal_column_as_a_number()
    {
        var offenders = Statements()
            .SelectMany(statement => statement.Items
                .Where(item => item.Kind is not (ItemKind.Text or ItemKind.NotDecimal))
                .Select(item => $"{statement.Where}: {Describe(item)}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A stored SAP query selects a decimal as a number, which can leave a Service Layer worker stuck for good. "
            + "Select it with SapSqlAmount.Column, or SapSqlAmount.Sum for a total, and read it with SapSqlAmount.Read:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_scan_reads_the_statements_it_is_there_for()
    {
        // If the scan stops finding statements, the test above passes on nothing.
        var statements = Statements();

        Assert.True(statements.Count >= 45, $"Only {statements.Count} stored SAP queries were found in the source.");
        // DSK_INV_COST_RANGE and the per-warehouse stock read, the two SAP was found stuck on.
        Assert.Contains(statements, statement => statement.Sql.Contains("LEFT(T0.\"GrssProfit\", 40) AS \"GrssProfit\"", StringComparison.Ordinal));
        Assert.Contains(statements, statement => statement.Sql.Contains("LEFT(T1.\"OnHand\", 40) AS \"InStock\"", StringComparison.Ordinal));
        // A total, as the reports select them.
        Assert.Contains(statements, statement => statement.Sql.Contains("LEFT(SUM(T1.\"GrssProfit\"), 40) AS \"GrossProfit\"", StringComparison.Ordinal));
        // Every statement Convert-SapAmountsToText.py converted: 25 with a decimal column and 17 with a
        // total. One more is one more statement selecting an amount the right way; fewer means a
        // statement was lost to the scan.
        Assert.Equal(42, statements.Count(statement => statement.Items.Any(item => item.Kind == ItemKind.Text)));
    }

    [Theory]
    [InlineData("""SELECT T0."ItemCode", T0."Quantity" FROM INV1 T0""", ItemKind.Decimal)]
    [InlineData("""SELECT T0."ItemCode", T1."OnHand" as "InStock" FROM OITW T1""", ItemKind.Decimal)]
    [InlineData("""SELECT T0."ItemCode", SUM(T0."LineTotal") AS "Total" FROM INV1 T0 GROUP BY T0."ItemCode" """, ItemKind.DecimalAggregate)]
    [InlineData("""SELECT T0."ItemCode", T0."U_SomethingNew" FROM OITM T0""", ItemKind.Unclassified)]
    [InlineData("""SELECT T0."ItemCode", ROUND(T0."Price", 2) AS "Price" FROM ITM1 T0""", ItemKind.Unrecognised)]
    [InlineData("""SELECT T0."ItemCode", MAX(T0."Price") AS "Price" FROM ITM1 T0 GROUP BY T0."ItemCode" """, ItemKind.Decimal)]
    public void The_scan_catches_a_decimal_however_it_is_selected(string sql, ItemKind expected)
    {
        Assert.Contains(SelectItems(sql), item => item.Kind == expected);
    }

    [Theory]
    [InlineData("""SELECT T0."ItemCode", LEFT(T0."Quantity", 40) AS "Quantity" FROM INV1 T0 WHERE T0."Quantity" > 0""")]
    [InlineData("""SELECT DISTINCT T0."DocEntry" AS "InvoiceDocEntry" FROM INV1 T0""")]
    [InlineData("""SELECT TOP 1 T0."Currency", T0."RateDate", LEFT(T0."Rate", 40) AS "Rate" FROM ORTT T0""")]
    [InlineData("""SELECT T0."DocDate" AS "DocDate", LEFT(SUM(T1."LineTotal"), 40) AS "Revenue" FROM OINV T0 GROUP BY T0."DocDate" """)]
    [InlineData("""SELECT T1."ItemCode", MAX(T0."DocDate") AS "LastSoldDate", COUNT(DISTINCT T0."DocEntry") as "Invoices", 'USD' AS "Currency" FROM OINV T0""")]
    public void The_scan_passes_text_whole_numbers_dates_and_a_decimal_in_the_where(string sql)
    {
        Assert.DoesNotContain(
            SelectItems(sql),
            item => item.Kind is not (ItemKind.Text or ItemKind.NotDecimal));
    }

    public enum ItemKind
    {
        /// <summary>Selected with <c>LEFT(…, 40)</c>.</summary>
        Text,
        NotDecimal,
        Decimal,
        DecimalAggregate,
        /// <summary>A column in neither list above.</summary>
        Unclassified,
        /// <summary>An expression this scan does not know how to judge.</summary>
        Unrecognised,
    }

    public sealed record SelectItem(string Expression, string Alias, ItemKind Kind);

    private sealed record Statement(string File, int Line, string Sql, IReadOnlyList<SelectItem> Items)
    {
        public string Where => $"{Path.GetFileName(File)}:{Line}";
    }

    private static string Describe(SelectItem item) => item.Kind switch
    {
        ItemKind.Decimal => $"{item.Expression} is a decimal selected as a number",
        ItemKind.DecimalAggregate => $"{item.Expression} is a total selected as a number; use SapSqlAmount.Sum",
        ItemKind.Unclassified => $"{item.Expression} is in neither list of this test; check its type in SAP and classify it",
        _ => $"{item.Expression} is not an expression this test can judge",
    };

    // Comments first, so a quote inside one starts nothing; then each kind of string literal. An
    // interpolated literal's holes may hold quotes of their own.
    private static readonly Regex Token = new(
        """"
        (?<lc>//[^\n]*)
        |(?<bc>/\*.*?\*/)
        |(?<raw>\$?"""(?<rawb>.*?)""")
        |(?<iverb>(?:\$@|@\$)"(?<iverbb>(?:[^"{]|""|\{\{|\{[^{}]*\})*)")
        |(?<verb>@"(?<verbb>(?:[^"]|"")*)")
        |(?<ireg>\$"(?<iregb>(?:[^"\\\n{]|\\.|\{\{|\{[^{}]*\})*)")
        |(?<reg>"(?<regb>(?:[^"\\\n]|\\.)*)")
        |(?<ch>'(?:[^'\\\n]|\\.)')
        """",
        RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex AmountHole = new("""\{SapSqlAmount\.(Column|Sum)\("(\w+)", "(\w+)"(?:, "(\w+)")?\)\}""");
    private static readonly Regex FragmentHole = new(@"\{(\w+)\}");

    // A SAP table is four capitals and digits (OINV, INV1, ITM1), quoted or not, or a table name passed in.
    private static readonly Regex FromSapTable = new("""\bFROM\s+(?:"?[A-Z][A-Z0-9]{3}"?(?![A-Za-z0-9_"])|\{\w+Table\})""");
    private static readonly Regex SelectList = new(@"\bSELECT\b(?<list>.*?)\bFROM\b", RegexOptions.Singleline);
    private static readonly Regex Aliased = new(
        """^(?<expression>.+?)(?:\s+(?:AS\s+)?"(?<alias>\w+)")?$""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Column = new("""^(?:\w+\.)?"(?<column>\w+)"$""");

    private static List<Statement> Statements()
    {
        var root = Path.Combine(RepositoryRoot(), "ShopInventory");
        var statements = new List<Statement>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (Match token in Token.Matches(source))
            {
                var sql = SqlOf(token);
                if (sql is null || !sql.Contains("SELECT", StringComparison.Ordinal) || !FromSapTable.IsMatch(sql))
                {
                    continue;
                }

                var line = source.AsSpan(0, token.Index).Count('\n') + 1;
                statements.Add(new Statement(relative, line, sql, SelectItems(sql)));
            }
        }

        return statements;
    }

    /// <summary>The SQL a string literal holds once the C# around it is taken away, or null for a comment or a char.</summary>
    private static string? SqlOf(Match token)
    {
        string body;
        if (token.Groups["raw"].Success)
        {
            body = token.Groups["rawb"].Value;
        }
        else if (token.Groups["iverb"].Success || token.Groups["verb"].Success)
        {
            body = (token.Groups["iverb"].Success ? token.Groups["iverbb"] : token.Groups["verbb"]).Value
                .Replace("\"\"", "\"", StringComparison.Ordinal);
        }
        else if (token.Groups["ireg"].Success || token.Groups["reg"].Success)
        {
            body = (token.Groups["ireg"].Success ? token.Groups["iregb"] : token.Groups["regb"]).Value
                .Replace("\\\"", "\"", StringComparison.Ordinal);
        }
        else
        {
            return null;
        }

        return AmountHole.Replace(body, hole => (hole.Groups[1].Value, hole.Groups[4].Success) switch
        {
            ("Sum", true) => SapSqlAmount.Sum(hole.Groups[2].Value, hole.Groups[3].Value, hole.Groups[4].Value),
            ("Column", true) => SapSqlAmount.Column(hole.Groups[2].Value, hole.Groups[3].Value, hole.Groups[4].Value),
            ("Column", false) => SapSqlAmount.Column(hole.Groups[2].Value, hole.Groups[3].Value),
            _ => hole.Value,
        });
    }

    private static List<SelectItem> SelectItems(string sql)
    {
        var items = new List<SelectItem>();
        foreach (Match list in SelectList.Matches(sql))
        {
            var text = FragmentHole.Replace(
                list.Groups["list"].Value,
                hole => SelectListFragments.TryGetValue(hole.Groups[1].Value, out var fragment) ? fragment : hole.Value);

            foreach (var raw in SplitAtTopLevelCommas(text))
            {
                var item = Regex.Replace(raw.Trim(), @"^(?:DISTINCT\s+|TOP\s+\d+\s+)+", string.Empty, RegexOptions.IgnoreCase);
                if (item.Length == 0)
                {
                    continue;
                }

                var parts = Aliased.Match(item);
                var expression = parts.Groups["expression"].Value.Trim();
                var alias = parts.Groups["alias"].Success ? parts.Groups["alias"].Value : expression;
                items.Add(new SelectItem(expression, alias, Classify(expression)));
            }
        }

        return items;
    }

    private static ItemKind Classify(string expression)
    {
        if (Regex.IsMatch(expression, $@"^LEFT\(.+, {SapSqlAmount.TextLength}\)$", RegexOptions.Singleline))
        {
            return ItemKind.Text;
        }

        if (Regex.IsMatch(expression, @"^'[^']*'$") || Regex.IsMatch(expression, @"^COUNT\(", RegexOptions.IgnoreCase))
        {
            return ItemKind.NotDecimal;
        }

        if (Regex.IsMatch(expression, @"^(?:SUM|AVG)\(", RegexOptions.IgnoreCase))
        {
            return ItemKind.DecimalAggregate;
        }

        var extreme = Regex.Match(expression, @"^(?:MAX|MIN)\((?<inner>.+)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (extreme.Success)
        {
            return Classify(extreme.Groups["inner"].Value.Trim());
        }

        var column = Column.Match(expression);
        if (!column.Success)
        {
            return ItemKind.Unrecognised;
        }

        var name = column.Groups["column"].Value;
        return Decimal.Contains(name) ? ItemKind.Decimal
            : NotDecimal.Contains(name) ? ItemKind.NotDecimal
            : ItemKind.Unclassified;
    }

    private static IEnumerable<string> SplitAtTopLevelCommas(string text)
    {
        var depth = 0;
        var inLiteral = false;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\'')
            {
                inLiteral = !inLiteral;
            }
            else if (!inLiteral && character == '(')
            {
                depth++;
            }
            else if (!inLiteral && character == ')')
            {
                depth--;
            }
            else if (!inLiteral && depth == 0 && character == ',')
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        yield return text[start..];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("ShopInventory.sln was not found above the test binaries.");
    }
}
