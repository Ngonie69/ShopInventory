using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ShopInventory.Web.Services;

public sealed class WebClientAuditCircuitHandler(
    WebClientAuditContext clientAuditContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<WebClientAuditCircuitHandler> logger) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CaptureClientContext();
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CaptureClientContext();
        return Task.CompletedTask;
    }

    private void CaptureClientContext()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            logger.LogDebug("Web client audit context was not captured because HttpContext was unavailable.");
            return;
        }

        clientAuditContext.Capture(httpContext);
    }
}
