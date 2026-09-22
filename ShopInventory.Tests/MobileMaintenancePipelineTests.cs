namespace ShopInventory.Tests;

/// <summary>
/// Where the maintenance lockout sits in the request pipeline.
///
/// <para>
/// The gate's own tests prove what it decides and the middleware's prove how it answers. Neither
/// can see the one thing that decides whether any of it runs: the order of the <c>app.Use…</c>
/// calls in <c>Program.cs</c>. Middleware ordering is ordinary-looking code that no compiler and no
/// unit test checks, and getting it wrong here does not break anything visibly — it leaves a
/// lockout that quietly only applies to requests that already authenticated.
/// </para>
/// </summary>
public sealed class MobileMaintenancePipelineTests
{
    [Fact]
    public void The_lockout_runs_before_authentication()
    {
        // A lockout that only applied to authenticated requests would be a lockout with a hole in
        // it, and authenticating a phone first tells us nothing the decision needs: it turns on the
        // app's headers and the operator's switch, not on who is holding the handset.
        var program = ReadProgram();

        var lockout = program.IndexOf("app.UseMobileMaintenance()", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);

        Assert.True(lockout >= 0, "Program.cs no longer registers the mobile maintenance lockout.");
        Assert.True(authentication >= 0, "Program.cs no longer calls UseAuthentication.");
        Assert.True(
            lockout < authentication,
            "UseMobileMaintenance must run before UseAuthentication, or the lockout only applies to "
            + "requests that already carry a valid token.");
    }

    [Fact]
    public void The_lockout_runs_after_cors()
    {
        // A 503 the browser-side of an app cannot read is a spinner that never stops. CORS headers
        // have to be on the refusal too.
        var program = ReadProgram();

        var cors = program.IndexOf("app.UseCors(", StringComparison.Ordinal);
        var lockout = program.IndexOf("app.UseMobileMaintenance()", StringComparison.Ordinal);

        Assert.True(cors >= 0, "Program.cs no longer calls UseCors.");
        Assert.True(cors < lockout, "UseMobileMaintenance must run after UseCors.");
    }

    [Fact]
    public void The_stored_switch_is_loaded_before_the_first_request()
    {
        // The deploy a node is coming up from may well be the maintenance itself. A node that
        // started clean while the switch was on would accept transactions for its first few
        // seconds, which is exactly the window somebody is running a migration in.
        var program = ReadProgram();

        Assert.Contains("IMobileMaintenanceStore>().ReloadAsync(", program, StringComparison.Ordinal);
    }

    private static string ReadProgram()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "ShopInventory", "Program.cs");
        Assert.True(File.Exists(path), $"Program.cs is not at {path}.");

        return File.ReadAllText(path);
    }
}
