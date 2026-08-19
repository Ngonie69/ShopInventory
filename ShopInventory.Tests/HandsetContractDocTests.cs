using System.Reflection;
using System.Text.Json.Serialization;
using ShopInventory.DTOs;

namespace ShopInventory.Tests;

/// <summary>
/// <c>docs/fiscalisation-handset-contract.md</c> is the specification a separate team builds the handset
/// against, so a wrong statement in it is a bug that ships in someone else's repository.
/// </summary>
/// <remarks>
/// Two kinds of wrongness were found, and only one of them is the sort a reader could catch.
///
/// <para>
/// The first was a field list that had drifted from the DTOs: §6.1 named <c>total</c> and
/// <c>vat_amount</c> among the receipt-level fields, and <see cref="VanSalesOrderRequest"/> carries
/// neither, so a handset team implementing §6 would have sent two fields into nothing. That is
/// mechanically checkable and is checked here — the test reads the JSON names off the DTOs, so the
/// document cannot drift from them again without this failing.
/// </para>
///
/// <para>
/// The second was a rationale, and no test can check that a reason is true. What it can do is pin the
/// specific false claim so it cannot come back: the document said a missing line <c>description</c> makes
/// the signature fail. It does not — the line name is not in the canonical payload
/// (<c>ReceiptCanonicalPayload</c> takes deviceId, receiptType, currency, globalNo, receiptDate, total,
/// the tax block and the previous hash, and nothing off a line but its contribution to those). The
/// requirement stands; the reason is a preflight block and a platform null-ref.
/// </para>
/// </remarks>
public sealed class HandsetContractDocTests
{
    private static readonly string Contract = ReadContract();

    // ── §6.1 must describe the DTOs that exist ──────────────────────────────

    /// <summary>
    /// A field the document lists as receipt-level but the online DTO cannot bind is a field the handset
    /// team would send into nothing — no error, no warning, silently dropped by the deserializer.
    /// </summary>
    [Theory]
    [InlineData("total")]
    [InlineData("vat_amount")]
    public void A_field_absent_from_the_online_dto_is_not_listed_as_common(string jsonName)
    {
        // The premise: these really are offline-only. If the online DTO ever gains them, this test is
        // telling you to move the rows back into the common table rather than to delete the assertion.
        Assert.DoesNotContain(jsonName, JsonNamesOf<VanSalesOrderRequest>());
        Assert.Contains(jsonName, JsonNamesOf<VanSalesOfflineSaleRequest>());

        var common = Section(Contract, "### 6.1 Receipt-level fields", "The table above is the set");

        Assert.DoesNotContain($"| `{jsonName}` |", common);
        Assert.Contains($"| `{jsonName}` |", Contract);
        Assert.Contains("offline only", Contract);
    }

    /// <summary>
    /// The other direction: every field §6.1 does present as common has to be on both DTOs, or the
    /// document is understating the contract instead of overstating it.
    /// </summary>
    [Fact]
    public void Every_field_listed_as_common_is_on_both_dtos()
    {
        var online = JsonNamesOf<VanSalesOrderRequest>();
        var offline = JsonNamesOf<VanSalesOfflineSaleRequest>();

        var listed = Section(Contract, "### 6.1 Receipt-level fields", "The table above is the set")
            .Split('\n')
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('`')[1])
            .ToList();

        Assert.NotEmpty(listed);

        foreach (var field in listed)
        {
            Assert.True(online.Contains(field), $"§6.1 lists `{field}` but {nameof(VanSalesOrderRequest)} has no such JSON name.");
            Assert.True(offline.Contains(field), $"§6.1 lists `{field}` but {nameof(VanSalesOfflineSaleRequest)} has no such JSON name.");
        }
    }

    // ── The `description` rule keeps its requirement and loses its false reason ──

    /// <summary>
    /// Still required. The correction was to the reason, not to the rule, and a fix that quietly relaxed
    /// the requirement would be worse than the wrong reason was.
    /// </summary>
    [Fact]
    public void The_description_field_is_still_required()
    {
        var rule = LineContaining("| `description` |");

        Assert.Contains("Required", rule, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The line name is not a component of the canonical payload, so a missing description cannot break a
    /// signature. Saying it does sends the handset team looking at their signing routine for a fault that
    /// is a null-ref on the server.
    /// </summary>
    [Fact]
    public void The_description_rule_does_not_blame_the_signature()
    {
        var rule = LineContaining("| `description` |");

        Assert.DoesNotContain("will not verify", rule);
        Assert.DoesNotContain("verification failure", rule.Replace("not a verification failure", string.Empty));

        // What it is instead: blocked locally, and unguarded on the platform past that.
        Assert.Contains("preflight", rule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null-ref", rule, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rollout checklist repeats the rule, so it repeated the wrong reason too.</summary>
    [Fact]
    public void The_rollout_checklist_does_not_repeat_the_false_reason()
    {
        var item = LineContaining("Stamp the online direct-invoice path");

        Assert.Contains("description", item);
        Assert.DoesNotContain("will not verify", item);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static HashSet<string> JsonNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal)!;

    private static string Section(string document, string heading, string until)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The contract no longer contains the heading '{heading}'.");

        var end = document.IndexOf(until, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{until}' no longer follows '{heading}'.");

        return document[start..end];
    }

    private static string LineContaining(string needle)
    {
        var line = Contract
            .Split('\n')
            .SingleOrDefault(candidate => candidate.Contains(needle, StringComparison.Ordinal));

        Assert.NotNull(line);
        return line!;
    }

    private static string ReadContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "docs", "fiscalisation-handset-contract.md");
        Assert.True(File.Exists(path), $"The handset contract is not at {path}.");

        return File.ReadAllText(path);
    }
}
