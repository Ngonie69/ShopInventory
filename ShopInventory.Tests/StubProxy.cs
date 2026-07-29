using System.Reflection;

namespace ShopInventory.Tests;

/// <summary>
/// Builds a stand-in for an interface from a handler function, for interfaces too wide to stub by
/// hand — <see cref="ShopInventory.Services.ISAPServiceLayerClient"/> alone has well over a hundred
/// members. Any member the handler does not answer throws, so a test fails loudly rather than
/// silently observing a default.
/// </summary>
public class StubProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _handler = null!;
    private string _interfaceName = "interface";

    public static TInterface For<TInterface>(
        Func<MethodInfo, object?[]?, object?> handler)
        where TInterface : class
    {
        var proxy = Create<TInterface, StubProxy>()!;
        var stub = (StubProxy)(object)proxy;
        stub._handler = handler;
        stub._interfaceName = typeof(TInterface).Name;
        return proxy;
    }

    /// <summary>A stand-in that fails the test if anything on it is called.</summary>
    public static TInterface Unused<TInterface>()
        where TInterface : class =>
        For<TInterface>((method, _) => throw new InvalidOperationException(
            $"{typeof(TInterface).Name}.{method.Name} was not expected to be called."));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = _handler(targetMethod!, args);

        if (result is null && targetMethod!.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"{_interfaceName}.{targetMethod.Name} was called but the stub has no answer for it.");
        }

        return result;
    }
}
