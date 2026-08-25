using System.Reflection;
using System.Text.RegularExpressions;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Keeps both-bound <c>BETWEEN</c> out of the SQL sent to SAP's SQLQueries endpoint.
/// </summary>
/// <remarks>
/// SAP's validator strips the whitespace out of a statement before parsing it, so
/// <c>x BETWEEN :a AND :b</c> reaches the grammar as <c>xBETWEEN:aAND:b</c> and is refused with
/// error 701, "Invalid parameterized expression". Two single-parameter comparisons say the same
/// thing and are the shape every working report already uses.
///
/// What makes it worth a test is that the failure is invisible from the outside. The SQL is valid
/// HANA and reads correctly in review; nothing rejects it until a live SAP does, and each caller
/// catches the exception and falls back — so the feature keeps answering, more slowly and with
/// gaps, while the log fills with warnings. One production log carried 148 of these in four hours
/// across two unrelated features, both introduced by the same reasonable-looking clause.
///
/// Reflected rather than grepped because these constants compose: the credit-note pair is built
/// from a shared template at type-initialisation, so the offending text never appears whole in any
/// source line. The assembly is the only place the real statement exists.
/// </remarks>
public class SapSqlBetweenTests
{
    private static readonly Regex BothBoundBetween = new(
        @"BETWEEN\s+:\w+\s+AND\s+:\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void No_sap_sql_uses_between_with_two_bound_parameters()
    {
        var statements = AllSqlStatements();

        // A guard that reflects over nothing passes for the wrong reason.
        Assert.NotEmpty(statements);

        var offenders = statements
            .Where(statement => BothBoundBetween.IsMatch(statement.Sql))
            .Select(statement => $"{statement.Origin}: {BothBoundBetween.Match(statement.Sql).Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "SAP rejects BETWEEN with two bound parameters (error 701). Use `col >= :a AND col <= :b`:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every string constant and static readonly string in the API assembly that looks like SQL.
    /// </summary>
    private static List<(string Origin, string Sql)> AllSqlStatements()
    {
        var found = new List<(string, string)>();

        foreach (var type in typeof(SAPServiceLayerClient).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (field.FieldType != typeof(string))
                    continue;

                string? value;
                try
                {
                    // Reading a static readonly field runs the type initialiser, which for an
                    // unrelated type may need configuration this test does not have.
                    value = field.GetValue(null) as string;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!value.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                    continue;

                found.Add(($"{type.Name}.{field.Name}", value));
            }
        }

        return found;
    }
}
