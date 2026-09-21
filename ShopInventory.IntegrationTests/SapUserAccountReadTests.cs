using ShopInventory.Models;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept the reads behind the SAP user administration page: the
/// account list, the search and lock filters it narrows with, and one account by key.
/// </summary>
/// <remarks>
/// <b>Read-only, and this one has to stay that way more firmly than most.</b> The two writes the
/// page performs — clearing a lock and setting a password — change real sign-ins on a company shared
/// with live users. There is no harmless version of "set a password and put it back", because the
/// original is not readable. They are proven by hand against the test company, never here.
///
/// <para>
/// What these settle that nothing offline can: <c>SapSelectClauseTests</c> proves every field named
/// in the <c>$select</c> exists on SAP's <c>User</c> type, but SAP validates <c>$select</c> against
/// the entity set at runtime and has refused fields the shared type declares. The filters are the
/// other half — <c>Locked eq 'tYES'</c> is an enum compared to a quoted literal, and
/// <c>contains()</c> over two string columns, neither of which the metadata speaks to.
/// </para>
/// </remarks>
[Collection("SAP")]
public class SapUserAccountReadTests(SapClientFixture fixture)
{
    [SapFact]
    public async Task The_user_account_list_is_accepted_and_carries_the_lock_state()
    {
        var accounts = await fixture.Client.GetSapUserAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.All(accounts, account =>
        {
            Assert.True(account.InternalKey > 0, "SAP returned a user account with no InternalKey.");
            Assert.False(string.IsNullOrWhiteSpace(account.UserCode), "SAP returned a user account with no UserCode.");

            // The whole page turns on this field. A Service Layer that answered null here would
            // leave every account reading as unlocked, and the page would quietly have nothing to do.
            Assert.True(
                account.Locked is SapYesNo.Yes or SapYesNo.No,
                $"SAP user {account.UserCode} reports Locked as '{account.Locked}', which is neither {SapYesNo.Yes} nor {SapYesNo.No}.");
        });
    }

    /// <summary>
    /// The locked filter is an enum compared to a quoted literal. A Service Layer that refuses that
    /// form answers 400 and the page's only useful view is the one that breaks.
    /// </summary>
    [SapFact]
    public async Task The_locked_filter_is_accepted_and_returns_only_locked_accounts()
    {
        var locked = await fixture.Client.GetSapUserAccountsAsync(lockedOnly: true);

        // An empty result is not a failure — a company need not have anybody locked out today.
        Assert.All(locked, account => Assert.True(
            account.IsLocked,
            $"SAP user {account.UserCode} came back under Locked eq '{SapYesNo.Yes}' but reports '{account.Locked}'."));
    }

    /// <summary>
    /// <c>contains()</c> over <c>UserCode</c> and <c>UserName</c> together. Searched on a code SAP
    /// itself returned, so the assertion is about the filter being honoured rather than about any
    /// particular company having a user by that name.
    /// </summary>
    [SapFact]
    public async Task The_search_filter_is_accepted_and_narrows_the_list()
    {
        var all = await fixture.Client.GetSapUserAccountsAsync();
        var first = all.First(account => !string.IsNullOrWhiteSpace(account.UserCode));

        var found = await fixture.Client.GetSapUserAccountsAsync(search: first.UserCode);

        Assert.Contains(found, account => account.InternalKey == first.InternalKey);
        Assert.True(
            found.Count <= all.Count,
            $"Searching for '{first.UserCode}' returned {found.Count} accounts, more than the unfiltered {all.Count}.");
    }

    [SapFact]
    public async Task One_user_account_is_read_by_its_internal_key()
    {
        var all = await fixture.Client.GetSapUserAccountsAsync();
        var first = all.First();

        var account = await fixture.Client.GetSapUserAccountAsync(first.InternalKey);

        Assert.NotNull(account);
        Assert.Equal(first.InternalKey, account.InternalKey);
        Assert.Equal(first.UserCode, account.UserCode);
    }
}
