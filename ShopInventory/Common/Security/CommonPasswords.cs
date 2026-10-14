#if SHOPINVENTORY_WEB
namespace ShopInventory.Web.Common.Security;
#else
namespace ShopInventory.Common.Security;
#endif

/// <summary>
/// The passwords everybody tries first.
/// </summary>
/// <remarks>
/// Lower case, and mostly <em>stems</em> rather than whole passwords: <see cref="PasswordPolicy"/>
/// peels the digits and punctuation people add to get past a length rule, and undoes the usual
/// letter swaps, before it looks here. So <c>welcome</c> also refuses <c>Welcome1</c>,
/// <c>W3lcome!</c> and <c>welcome2026</c>, and the list does not need a line for each.
/// <para>
/// Entries that are themselves made of digits and letters (<c>1q2w3e4r</c>, <c>abc123</c>) are here
/// whole, because peeling would leave nothing to match.
/// </para>
/// <para>
/// The last block is this company's own names. They are not on anybody's leaked list, and they are
/// the first thing someone standing in a depot would try.
/// </para>
/// </remarks>
internal static class CommonPasswords
{
    public static readonly HashSet<string> Stems = new(
        """
        password passwd passcode passphrase pass pword secret letmein letmeinnow welcome welcomehome
        login logon signin admin administrator root sysadmin superuser supervisor manager master
        default guest user username test testing tester temp temporary changeme changeit newpassword
        mypassword yourpassword nopassword thepassword passwordpassword access accessgranted qwerty
        qwertyuiop qwertyui qwert qwer asdf asdfgh asdfghjkl asdfasdf zxcv zxcvbn zxcvbnm qazwsx
        qazwsxedc qweasd qweasdzxc qwaszx poiuyt lkjhgf mnbvcx azerty abc abcd abcde abcdef abcdefg
        abcabc aaa aaaa abcd1234 abc123 abc1234 a1b2c3 a1b2c3d4 1q2w3e 1q2w3e4r 1q2w3e4r5t 1qaz2wsx
        1qazxsw2 q1w2e3 q1w2e3r4 q1w2e3r4t5 zaq12wsx zaq1xsw2 123qwe 123abc 1234qwer qwer1234
        qwerty123 qwe123 pass1234 pass123 123123 123321 112233 11223344 121212 12341234 123456
        1234567 12345678 123456789 1234567890 0123456789 987654321 87654321 654321 000000 111111
        222222 333333 444444 555555 666666 777777 888888 999999 696969 101010 131313 232323
        iloveyou iloveu loveyou loveme lovely love lover sweetheart darling honey sweetie babygirl
        baby angel princess prince queen king beautiful gorgeous handsome sexy hottie cutie
        sunshine summer winter spring autumn monday tuesday wednesday thursday friday saturday
        sunday january february march april june july august september october november december
        monkey dragon tiger lion eagle falcon panther jaguar cheetah leopard buffalo elephant rhino
        giraffe zebra shark dolphin butterfly flower rose daisy lily jasmine orchid chocolate cookie
        banana orange apple mango cherry pepper ginger cheese coffee
        football soccer baseball basketball cricket rugby hockey tennis golf arsenal chelsea
        liverpool manchester manutd united barcelona realmadrid juventus madrid dynamos highlanders
        caps capsunited warriors chiefs pirates sundowns lakers cowboys yankees
        superman batman spiderman ironman starwars pokemon naruto matrix ninja samurai wizard
        hunter killer shadow ghost phantom legend champion winner trustno freedom liberty justice
        hello hellohello hi goodbye whatever nothing something anything everything blessed blessing
        blessings jesus jesuschrist christ god godisgood godislove faith grace mercy hope glory
        heaven amen emmanuel hallelujah praise church bible
        michael michelle jennifer jessica ashley amanda daniel david joshua matthew andrew james
        john robert thomas charlie george harry william jordan taylor nicole hannah samantha
        tendai tatenda tapiwa tinashe tafadzwa takudzwa tanaka farai kudzai rudo chipo nyasha
        tonderai blessing memory precious patience prosper brighton trust gift
        computer internet laptop desktop windows microsoft google samsung nokia iphone android
        mobile phone office server system network security secure online website email facebook
        whatsapp twitter instagram youtube
        money dollar dollars cash bank account business company work working job boss staff
        mother father mommy daddy mum dad family friend friends brother sister
        zimbabwe harare bulawayo mutare gweru masvingo chitungwiza kwekwe kadoma victoriafalls
        africa african southafrica
        kefalos kefaloscheese cheeseman kefshop shopinventory sis siskefalos kefalosso vansales
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);
}
