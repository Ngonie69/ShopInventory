using System.Globalization;
using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer for what the credit-note projection sweep relies on: the header-only
/// version poll, a time of day in <c>UpdateTime</c>, and whole documents fetched back by DocEntry
/// agreeing with the version they were polled at.
/// </summary>
/// <remarks>
/// Read-only. The sweep fetches a document only when its version has moved. A poll refused outright
/// fails the sweep; one answering without <c>UpdateTime</c>, or with the same time for every
/// document, would make it refetch everything or miss a second edit on one day. The metadata
/// declares <c>UpdateTime</c> on the shared <c>Document</c> type, which does not prove
/// <c>CreditNotes</c> accepts it in a <c>$select</c>.
/// </remarks>
[Collection("SAP")]
public class SapCreditNoteVersionTests(SapClientFixture fixture, ITestOutputHelper output)
{
    [SapFact]
    public async Task Version_poll_returns_an_update_time_that_matches_the_whole_document()
    {
        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-365);

        var versions = await fixture.Client.GetCreditNoteVersionsUpdatedSinceAsync(from, to);
        output.WriteLine($"{versions.Count} credit note(s) updated {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        Assert.True(
            versions.Count > 0,
            "The test company has no credit note updated in the last year, so the poll cannot be checked. Point these tests at a company with data.");

        foreach (var version in versions.Take(5))
        {
            output.WriteLine(
                $"DocEntry {version.DocEntry}: UpdateDate {version.UpdateDate}, UpdateTime {version.UpdateTime}, " +
                $"{version.DocumentStatus}, Cancelled {version.Cancelled}, DocTotal {version.DocTotal}, " +
                $"lines {(version.DocumentLines is null ? "none" : version.DocumentLines.Count.ToString(CultureInfo.InvariantCulture))}");
        }

        Assert.All(versions, version =>
        {
            Assert.True(version.DocEntry > 0);
            Assert.False(string.IsNullOrWhiteSpace(version.UpdateDate));
            Assert.True(
                TimeOnly.TryParse(version.UpdateTime, CultureInfo.InvariantCulture, out _),
                $"DocEntry {version.DocEntry} came back with UpdateTime '{version.UpdateTime}'.");
            Assert.True(version.DocumentLines is null || version.DocumentLines.Count == 0);
        });

        var distinctTimes = versions.Select(version => version.UpdateTime).Distinct().Count();
        output.WriteLine($"{distinctTimes} distinct UpdateTime value(s)");
        Assert.True(
            versions.Count < 2 || distinctTimes > 1,
            "Every credit note came back with the same UpdateTime, so it cannot tell two edits on one day apart.");

        var sample = versions.OrderByDescending(version => version.DocEntry).Take(25).ToList();
        var whole = await fixture.Client.GetCreditNotesByDocEntriesAsync(
            sample.Select(version => version.DocEntry).Append(int.MaxValue));

        Assert.Equal(
            sample.Select(version => version.DocEntry).Order(),
            whole.Select(note => note.DocEntry).Order());
        foreach (var note in whole)
        {
            var version = sample.Single(candidate => candidate.DocEntry == note.DocEntry);
            Assert.Equal(version.UpdateDate, note.UpdateDate);
            Assert.Equal(version.DocumentStatus, note.DocumentStatus);
            Assert.Equal(version.Cancelled, note.Cancelled);
            Assert.Equal(version.DocTotal, note.DocTotal);
            Assert.NotNull(note.DocumentLines);
        }
    }
}
