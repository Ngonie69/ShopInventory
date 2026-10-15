using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ShopInventory.Web.Services;

/// <summary>
/// Publishes the circuit's <see cref="WebApiCaller"/> around every inbound activity, so the API
/// clients' handlers can tell a circuit's requests from background work.
/// </summary>
/// <remarks>
/// Inbound activities are everything a circuit does on the browser's behalf: the first render of its
/// root components, events, navigation, JS interop calls and their replies. Work they start inherits
/// the value through its execution context.
/// </remarks>
public sealed class WebApiCallerCircuitHandler(WebApiCaller caller) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            WebApiCaller.Enter(caller);
            await next(context);
        };
}
