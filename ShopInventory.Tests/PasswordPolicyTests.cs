using ShopInventory.Common.Security;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the three rules of the password policy that can be decided from the password itself.
/// </summary>
/// <remarks>
/// The acceptances matter as much as the refusals. A rule that turns away a good password teaches
/// people to write the next one on a note beside the till, so every refusal here has a neighbour
/// that looks similar and must still be let through.
/// </remarks>
public sealed class PasswordPolicyTests
{
    private static readonly PasswordOwner Tendai = new()
    {
        Username = "t.moyo",
        Email = "tendai.moyo@kefaloscheese.com",
        FirstName = "Tendai",
        LastName = "Moyo",
        PhoneNumber = "+263 77 214 5580"
    };

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Xk9$mQ2")]
    public void A_password_under_eight_characters_is_refused(string password)
    {
        Assert.Contains(PasswordPolicy.TooShort, PasswordPolicy.Check(password));
    }

    [Fact]
    public void A_missing_password_is_refused_as_too_short_rather_than_throwing()
    {
        Assert.Equal([PasswordPolicy.TooShort], PasswordPolicy.Check(null, Tendai));
    }

    [Theory]
    [InlineData("river-stone-lantern")]
    [InlineData("Xk9$mQ2v")]
    [InlineData("correct horse battery staple")]
    [InlineData("mbira&granite42")]
    [InlineData("Maize7Thunder")]
    public void A_password_nobody_would_guess_is_accepted(string password)
    {
        Assert.Empty(PasswordPolicy.Check(password, Tendai));
    }

    [Theory]
    // The two the policy names.
    [InlineData("password")]
    [InlineData("12345678")]
    // Off the list as they stand.
    [InlineData("qwertyuiop")]
    [InlineData("iloveyou")]
    [InlineData("1q2w3e4r")]
    [InlineData("abc12345")]
    // Dressed up to get past a length or a character rule.
    [InlineData("Password1")]
    [InlineData("PASSWORD123!")]
    [InlineData("Welcome2026")]
    [InlineData("2026summer")]
    [InlineData("letmein!!")]
    [InlineData("admin1234")]
    // Look-alike letters.
    [InlineData("P@ssw0rd")]
    [InlineData("p@$$w0rd1")]
    [InlineData("h3ll0123")]
    [InlineData("W3lc0me!")]
    [InlineData("@dmin123")]
    // No information in them at all.
    [InlineData("aaaaaaaa")]
    [InlineData("12121212")]
    [InlineData("abcabcabc")]
    [InlineData("87654321")]
    [InlineData("7890123456")]
    [InlineData("abcdefgh")]
    [InlineData("99123456")]
    [InlineData("12345699")]
    // The company's own names.
    [InlineData("Kefalos2026")]
    [InlineData("cheeseman1")]
    [InlineData("K3fal0s!")]
    public void A_common_password_is_refused(string password)
    {
        Assert.Equal([PasswordPolicy.TooCommon], PasswordPolicy.Check(password));
    }

    [Theory]
    // A common word is only refused when it is the whole password.
    [InlineData("password-is-not-my-cat")]
    [InlineData("summer rain on a tin roof")]
    [InlineData("welcome2theJungle")]
    // Digits that are not a run.
    [InlineData("48213906")]
    [InlineData("1357924680")]
    public void A_password_that_only_resembles_a_common_one_is_accepted(string password)
    {
        Assert.Empty(PasswordPolicy.Check(password));
    }

    [Theory]
    [InlineData("tendai-river-9")]
    [InlineData("riverTENDAIstone")]
    [InlineData("moyo&lantern77")]
    [InlineData("xx-tmoyo-xx")]
    [InlineData("T3nd@i-lantern")]
    [InlineData("call0772145580")]
    [InlineData("263772145580x")]
    [InlineData("pin-2145580-pin")]
    [InlineData("tendai.moyo-granite")]
    public void A_password_holding_part_of_its_owners_identity_is_refused(string password)
    {
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check(password, Tendai));
    }

    [Fact]
    public void The_same_password_is_fine_for_somebody_else()
    {
        var rudo = new PasswordOwner { Username = "rudo.ncube", FirstName = "Rudo", LastName = "Ncube" };

        Assert.Empty(PasswordPolicy.Check("tendai-river-9", rudo));
    }

    [Fact]
    public void The_email_domain_is_not_held_against_a_password()
    {
        // Everybody shares it, so it identifies nobody. The company name alone is still refused, as
        // a common password.
        Assert.Empty(PasswordPolicy.Check("kefaloscheese-lantern-9", Tendai));
    }

    [Theory]
    // Two letters are inside too many ordinary words to be a fair refusal.
    [InlineData("Jo", "Li", "major-violin-9")]
    // A short run of digits in a username is not a phone number.
    [InlineData("till-12", null, "lantern-12-stone")]
    public void A_fragment_too_short_to_identify_anyone_is_ignored(string username, string? lastName, string password)
    {
        var owner = new PasswordOwner { Username = username, LastName = lastName };

        Assert.Empty(PasswordPolicy.Check(password, owner));
    }

    [Fact]
    public void Each_part_of_a_compound_username_counts()
    {
        var owner = new PasswordOwner { Username = "depot1-cashier" };

        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check("my-cashier-lantern", owner));
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check("lantern-depot1-x", owner));
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check("depot1cashier77", owner));
    }

    [Fact]
    public void Other_details_are_looked_for_too()
    {
        var owner = new PasswordOwner { Other = ["C00417", "Moyo General Dealers"] };

        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check("c00417-lantern", owner));
        Assert.Contains(PasswordPolicy.ContainsPersonalDetail, PasswordPolicy.Check("lantern-dealers-9", owner));
    }

    [Fact]
    public void Every_broken_rule_is_reported_at_once()
    {
        // Nobody should have to submit a form three times to learn three things.
        var broken = PasswordPolicy.Check("Tendai2026", Tendai);

        Assert.Equal([PasswordPolicy.TooCommon, PasswordPolicy.ContainsPersonalDetail], broken);
    }
}
