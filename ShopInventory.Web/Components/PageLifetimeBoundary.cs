using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components;

/// <summary>
/// Renews <see cref="PageLifetime"/> when the routed page's component type changes. Renders nothing.
/// </summary>
/// <remarks>
/// Sits in the router ahead of the route view, so it is given the new route before the new page is
/// created: the old page's reads are cancelled, and the new page binds to a fresh token. The same
/// component on a new route keeps its instance in Blazor, and keeps its token here.
/// </remarks>
public sealed class PageLifetimeBoundary : ComponentBase
{
    private Type? _pageType;

    [Inject]
    private PageLifetime PageLifetime { get; set; } = default!;

    [Parameter, EditorRequired]
    public Type PageType { get; set; } = default!;

    protected override void OnParametersSet()
    {
        if (_pageType is not null && _pageType != PageType)
        {
            PageLifetime.Renew();
        }

        _pageType = PageType;
    }
}
