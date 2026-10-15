namespace ShopInventory.Web.Services;

/// <summary>
/// Who a Blazor circuit is calling the API for: a signed-in member of staff, a signed-in portal
/// customer, or nobody. One per circuit.
/// </summary>
/// <remarks>
/// Every API client this app keys carries the Web's <c>X-API-Key</c>, and that key holds the Admin
/// and ApiUser roles so the Web's own work (Quartz jobs, cache sweeps, the customer portal) can run
/// without a person's token. The API answers a request carrying the key and no bearer token as the
/// key. A signed-in member of staff sends their token beside it and is judged as themselves. But a
/// circuit nobody is signed in on — a token cleared by the auth state provider, a stale tab — still
/// sends on the same clients, so whatever it asked for was answered with the key's Admin roles.
/// <para>
/// <see cref="CircuitApiKeyHandler"/> asks this, through <see cref="Current"/>, before a request
/// leaves: a request carrying no bearer token from a circuit with nobody signed in goes without the
/// key. Work outside any circuit — Quartz jobs, hosted services — has no <see cref="Current"/> and
/// keeps the key, as does work inside <see cref="AsTheWebApp"/>.
/// </para>
/// <para>
/// The circuit is published by <see cref="WebApiCallerCircuitHandler"/> around each inbound activity
/// (render, event, JS interop), as an <see cref="AsyncLocal{T}"/>. A handler built by
/// <see cref="IHttpClientFactory"/> lives in the factory's own DI scope and cannot resolve the
/// circuit's services (see <see cref="ClientAuditHeaders"/>), but it runs in the caller's execution
/// context and so sees the caller's <see cref="AsyncLocal{T}"/>. A timer, a hub callback or a
/// <c>Task.Run</c> started inside an activity captures it too.
/// </para>
/// <para>
/// Both answers are read from the services that already decide them, lazily, so a circuit that never
/// sends without a token never builds the portal's session service. A circuit's services remain
/// readable after the circuit is disposed; one first asked after disposal answers as no circuit,
/// which is how a send outside any circuit is treated.
/// </para>
/// </remarks>
public sealed class WebApiCaller(IServiceProvider circuitServices)
{
    private static readonly AsyncLocal<WebApiCaller?> Ambient = new();

    private CustomAuthStateProvider? _staff;
    private ICustomerPortalSessionService? _portal;

    /// <summary>The circuit the current code runs for, or null outside any circuit.</summary>
    public static WebApiCaller? Current => Ambient.Value;

    /// <summary>
    /// Whether this circuit has a member of staff the API confirmed, or a portal customer whose
    /// session was proved, so the Web's key may go out without a bearer token beside it.
    /// </summary>
    public bool HasSignedInCaller
    {
        get
        {
            try
            {
                _staff ??= circuitServices.GetRequiredService<CustomAuthStateProvider>();
                if (_staff.HasConfirmedUser)
                {
                    return true;
                }

                _portal ??= circuitServices.GetRequiredService<ICustomerPortalSessionService>();
                return _portal.HasVerifiedCustomer;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Runs the API requests made until the returned scope is disposed as the Web app itself, with
    /// the key, whoever the circuit is signed in as.
    /// </summary>
    /// <remarks>
    /// For a call the Web makes for its own purposes on a path nobody has signed in on yet, and whose
    /// answer the Web uses rather than shows: the business partner lookup behind a portal sign-in or
    /// registration. Not for sending something a page asked for.
    /// </remarks>
    public static IDisposable AsTheWebApp()
    {
        var previous = Ambient.Value;
        Ambient.Value = null;
        return new Scope(previous);
    }

    /// <summary>Publishes <paramref name="caller"/> for the rest of the current execution context.</summary>
    internal static void Enter(WebApiCaller caller) => Ambient.Value = caller;

    private sealed class Scope(WebApiCaller? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
