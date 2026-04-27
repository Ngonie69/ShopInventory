using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public sealed class AuthenticatedDownloadProxy(
    IHttpClientFactory httpClientFactory,
    ILogger<AuthenticatedDownloadProxy> logger)
{
    public async Task<IResult> ProxyAsync(
        HttpContext httpContext,
        string apiPath,
        string fallbackFileName,
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(httpContext, out var token))
        {
            return Results.Unauthorized();
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (!allowedRoles.Any(httpContext.User.IsInRole))
        {
            logger.LogWarning(
                "User {Username} attempted unauthorized download proxy access to {ApiPath}",
                httpContext.User.Identity.Name,
                apiPath);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var client = httpClientFactory.CreateClient("ShopInventoryApiUser");
        var request = new HttpRequestMessage(HttpMethod.Get, apiPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? fallbackFileName;

        return Results.File(stream, contentType, fileName);
    }

    private static bool TryGetBearerToken(HttpContext httpContext, out string token)
    {
        token = string.Empty;
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = NormalizeToken(authorization["Bearer ".Length..]);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static string NormalizeToken(string token)
    {
        token = token.Trim();
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(token)?.Trim() ?? string.Empty;
            }
            catch (JsonException)
            {
                return token.Trim('"').Trim();
            }
        }

        return token;
    }
}