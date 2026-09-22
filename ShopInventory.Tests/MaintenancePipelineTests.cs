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
///
/// <para>
/// There are two registrations now, and each has its own reason to be where it is: the early one
/// so a phone is refused without the API touching the database it is protecting, and the late one
/// so the web portal's Admin exemption has an authenticated user to look at. Both are asserted,
/// because either being dropped leaves a lockout that looks switched on and covers less than the
/// screen says.
/// </para>
/// </summary>
public sealed class MaintenancePipelineTests
{
    [Fact]
    public void The_lockout_runs_before_authentication()
    {
        // A lockout that only applied to authenticated requests would be a lockout with a hole in
        // it, and authenticating a phone first tells us nothing the decision needs: it turns on the
        // app's headers and the operator's switch, not on who is holding the handset.
        var program = ReadProgram();

        var lockout = program.IndexOf(
            "app.UseMaintenance(MaintenanceStage.BeforeAuthentication)", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);

        Assert.True(lockout >= 0, "Program.cs no longer registers the early maintenance lockout.");
        Assert.True(authentication >= 0, "Program.cs no longer calls UseAuthentication.");
        Assert.True(
            lockout < authentication,
            "The BeforeAuthentication stage must run before UseAuthentication, or the lockout only "
            + "applies to requests that already carry a valid token.");
    }

    [Fact]
    public void The_portals_half_of_the_lockout_runs_after_authorization()
    {
        // Not merely after UseAuthentication. The API's policies name their own schemes, so the
        // signed-in user's identity only reaches context.User once the authorization middleware has
        // run them — and until it does, every portal request looks like nobody, which is not an
        // Admin. The exemption would never fire and the people running the maintenance would be
        // locked out of fixing it.
        var program = ReadProgram();

        var lockout = program.IndexOf(
            "app.UseMaintenance(MaintenanceStage.AfterAuthorization)", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);

        Assert.True(lockout >= 0, "Program.cs no longer registers the post-authorization maintenance lockout.");
        Assert.True(authorization >= 0, "Program.cs no longer calls UseAuthorization.");
        Assert.True(
            authorization < lockout,
            "The AfterAuthorization stage must run after UseAuthorization, or context.User does not "
            + "yet hold the signed-in user and the Admin exemption never applies.");
    }

    [Fact]
    public void A_refused_request_is_not_recorded_or_served_from_cache()
    {
        // Ahead of idempotency, so a refusal does not claim the request's key and strand the retry
        // that follows it; ahead of the output cache, so a 503 is never handed to the next caller.
        var program = ReadProgram();

        var lockout = program.IndexOf(
            "app.UseMaintenance(MaintenanceStage.AfterAuthorization)", StringComparison.Ordinal);
        var idempotency = program.IndexOf("app.UseIdempotency()", StringComparison.Ordinal);
        var outputCache = program.IndexOf("app.UseOutputCache()", StringComparison.Ordinal);

        Assert.True(idempotency >= 0, "Program.cs no longer calls UseIdempotency.");
        Assert.True(outputCache >= 0, "Program.cs no longer calls UseOutputCache.");
        Assert.True(lockout < idempotency, "The lockout must run before UseIdempotency.");
        Assert.True(lockout < outputCache, "The lockout must run before UseOutputCache.");
    }

    [Fact]
    public void The_lockout_runs_after_cors()
    {
        // A 503 the browser-side of an app cannot read is a spinner that never stops. CORS headers
        // have to be on the refusal too.
        var program = ReadProgram();

        var cors = program.IndexOf("app.UseCors(", StringComparison.Ordinal);
        var lockout = program.IndexOf("app.UseMaintenance(", StringComparison.Ordinal);

        Assert.True(cors >= 0, "Program.cs no longer calls UseCors.");
        Assert.True(lockout >= 0, "Program.cs no longer registers the maintenance lockout.");
        Assert.True(cors < lockout, "UseMaintenance must run after UseCors.");
    }

    [Fact]
    public void The_stored_switch_is_loaded_before_the_first_request()
    {
        // The deploy a node is coming up from may well be the maintenance itself. A node that
        // started clean while the switch was on would accept transactions for its first few
        // seconds, which is exactly the window somebody is running a migration in.
        var program = ReadProgram();

        Assert.Contains("IMaintenanceStore>().ReloadAsync(", program, StringComparison.Ordinal);
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
