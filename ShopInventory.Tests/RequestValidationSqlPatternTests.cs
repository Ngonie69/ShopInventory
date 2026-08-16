using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the request-validation middleware's SQL-injection heuristic counts as a comment marker.
/// </summary>
/// <remarks>
/// It used to treat any "--" as one. Opaque base64url tokens carry "--" between letters and digits as a
/// matter of course, and on 2026-08-15 that blocked a live SignalR transport request
/// (?id=F9--6N_Qk3Nu71I4wABSTA) with a 400 and severed the user's hub connection. The classic payloads
/// all have the marker at a word boundary, and must still be caught.
/// </remarks>
public class RequestValidationSqlPatternTests
{
    [Theory]
    // The SignalR connection token that was blocked in production.
    [InlineData("?id=F9--6N_Qk3Nu71I4wABSTA")]
    // Other opaque tokens with the same shape.
    [InlineData("?token=eyJhbGciOi--JIUzI1NiJ9.abc")]
    [InlineData("?ref=a1--b2--c3")]
    [InlineData("?id=F9--6N_Qk3Nu71I4wABSTA&negotiateVersion=1")]
    public void ADoubleHyphenInsideAWordIsNotACommentMarker(string query)
    {
        Assert.False(RequestValidationMiddleware.IsMalicious(query, out var threat), threat);
    }

    [Theory]
    [InlineData("?user=admin'--", "SQLInjection")]
    [InlineData("?id=1 OR 1=1 --", "SQLInjection")]
    [InlineData("?id=1 OR 1=1--", "SQLInjection")]
    [InlineData("?q='; DROP TABLE users;--", "SQLInjection")]
    [InlineData("?q=-- anything", "SQLInjection")]
    [InlineData("?q=x' UNION SELECT password FROM users--", "SQLInjection")]
    public void ACommentMarkerAtAWordBoundaryIsStillCaught(string query, string expectedThreat)
    {
        Assert.True(RequestValidationMiddleware.IsMalicious(query, out var threat));
        Assert.Equal(expectedThreat, threat);
    }
}
