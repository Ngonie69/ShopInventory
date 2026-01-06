using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Authentication;

/// <summary>
/// Authorization attribute that requires specific permissions
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute : AuthorizeAttribute, IAuthorizationFilter
{
    private readonly string[] _permissions;
    private readonly bool _requireAll;

    /// <summary>
    /// Require specific permission(s)
    /// </summary>
    /// <param name="permissions">The permission(s) required</param>
    public RequirePermissionAttribute(params string[] permissions)
    {
        _permissions = permissions;
        _requireAll = false;
    }

    /// <summary>
    /// Require specific permission(s) with option to require all
    /// </summary>
    /// <param name="requireAll">If true, all permissions are required. If false, any one permission is sufficient.</param>
    /// <param name="permissions">The permission(s) required</param>
    public RequirePermissionAttribute(bool requireAll, params string[] permissions)
    {
        _permissions = permissions;
        _requireAll = requireAll;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Get user management service from DI
        var userManagementService = context.HttpContext.RequestServices
            .GetService<IUserManagementService>();

        if (userManagementService == null)
        {
            context.Result = new StatusCodeResult(500);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Get user's effective permissions
        var permissionsTask = userManagementService.GetEffectivePermissionsAsync(userId);
        permissionsTask.Wait();
        var userPermissions = permissionsTask.Result;

        // Check for system admin - they have all permissions
        if (userPermissions.Contains(Permission.SystemAdmin))
        {
            return; // Allow access
        }

        // Check if user has required permission(s)
        bool hasPermission;
        if (_requireAll)
        {
            hasPermission = _permissions.All(p => userPermissions.Contains(p));
        }
        else
        {
            hasPermission = _permissions.Any(p => userPermissions.Contains(p));
        }

        if (!hasPermission)
        {
            context.Result = new ForbidResult();
        }
    }
}

/// <summary>
/// Authorization requirement for permission-based access
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    public string[] Permissions { get; }
    public bool RequireAll { get; }

    public PermissionRequirement(string[] permissions, bool requireAll = false)
    {
        Permissions = permissions;
        RequireAll = requireAll;
    }
}

/// <summary>
/// Authorization handler for permission-based access
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IServiceProvider _serviceProvider;

    public PermissionAuthorizationHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var userManagementService = scope.ServiceProvider.GetRequiredService<IUserManagementService>();

        var userPermissions = await userManagementService.GetEffectivePermissionsAsync(userId);

        // System admin has all permissions
        if (userPermissions.Contains(Permission.SystemAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        bool hasPermission;
        if (requirement.RequireAll)
        {
            hasPermission = requirement.Permissions.All(p => userPermissions.Contains(p));
        }
        else
        {
            hasPermission = requirement.Permissions.Any(p => userPermissions.Contains(p));
        }

        if (hasPermission)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Extension methods for permission authorization
/// </summary>
public static class PermissionAuthorizationExtensions
{
    /// <summary>
    /// Add permission-based authorization policies
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // Add policies for each permission
            foreach (var permission in Permission.GetAllPermissions())
            {
                options.AddPolicy($"Permission:{permission}",
                    policy => policy.Requirements.Add(new PermissionRequirement(new[] { permission })));
            }

            // Common combined policies
            options.AddPolicy("CanManageProducts",
                policy => policy.Requirements.Add(new PermissionRequirement(
                    new[] { Permission.CreateProducts, Permission.EditProducts, Permission.DeleteProducts })));

            options.AddPolicy("CanManageInvoices",
                policy => policy.Requirements.Add(new PermissionRequirement(
                    new[] { Permission.CreateInvoices, Permission.EditInvoices, Permission.VoidInvoices })));

            options.AddPolicy("CanManageUsers",
                policy => policy.Requirements.Add(new PermissionRequirement(
                    new[] { Permission.CreateUsers, Permission.EditUsers, Permission.ManageUserRoles, Permission.ManageUserPermissions })));

            options.AddPolicy("CanViewReports",
                policy => policy.Requirements.Add(new PermissionRequirement(
                    new[] { Permission.ViewReports })));

            options.AddPolicy("AdminOnly",
                policy => policy.Requirements.Add(new PermissionRequirement(
                    new[] { Permission.SystemAdmin })));
        });

        return services;
    }
}
