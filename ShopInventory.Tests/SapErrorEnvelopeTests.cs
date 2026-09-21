using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP answers an error in two envelopes, and every rejection message this API shows a user is
/// pulled out of one of them. Reading only the v1 shape put the whole v2 body — braces, quotes and
/// an empty <c>details</c> array — in front of the user who tried to unlock an SAP account.
/// </summary>
public sealed class SapErrorEnvelopeTests
{
    [Fact]
    public void Reads_the_v2_envelope_where_the_message_is_a_string()
    {
        const string body = """
            { "error" : { "code" : "-5002", "details" : [ { "code" : "", "message" : "" } ], "message" : "Internal error (-5002) occurred" } }
            """;

        Assert.Equal("Internal error (-5002) occurred", SAPServiceLayerClient.ExtractSAPErrorMessage(body));
    }

    [Fact]
    public void Reads_the_v1_envelope_where_the_message_is_nested()
    {
        const string body = """
            { "error" : { "code" : -5002, "message" : { "lang" : "en-us", "value" : "Internal error (-5002) occurred" } } }
            """;

        Assert.Equal("Internal error (-5002) occurred", SAPServiceLayerClient.ExtractSAPErrorMessage(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("""{ "error" : { "code" : "-1" } }""")]
    public void Answers_null_for_anything_that_is_not_an_sap_envelope(string body)
    {
        Assert.Null(SAPServiceLayerClient.ExtractSAPErrorMessage(body));
    }
}
