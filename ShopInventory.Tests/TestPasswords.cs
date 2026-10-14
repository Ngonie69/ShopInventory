using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Password;

namespace ShopInventory.Tests;

/// <summary>
/// Builds the staff password writer for a fixture that has a context and no container.
/// </summary>
internal static class TestPasswords
{
    public static UserPasswords For(
        ApplicationDbContext context,
        PasswordPolicySettings? settings = null,
        TimeProvider? clock = null) =>
        new(context, Options.Create(settings ?? new PasswordPolicySettings()), clock ?? TimeProvider.System);
}
