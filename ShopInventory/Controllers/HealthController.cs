using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[AllowAnonymous]
public class HealthController(
    HealthCheckService healthCheckService,
    RuntimeInstanceIdentity runtimeInstanceIdentity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var liveness = await healthCheckService.CheckHealthAsync(
            registration => registration.Tags.Contains("live"),
            cancellationToken);
        var readiness = await healthCheckService.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"),
            cancellationToken);
        var dependencies = await healthCheckService.CheckHealthAsync(
            registration => registration.Tags.Contains("dependencies"),
            cancellationToken);

        var statusCode = readiness.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        return StatusCode(statusCode, new
        {
            Status = readiness.Status.ToString(),
            Timestamp = DateTime.UtcNow,
            Service = "ShopInventory API",
            Instance = new
            {
                runtimeInstanceIdentity.InstanceId,
                runtimeInstanceIdentity.MachineName,
                runtimeInstanceIdentity.ProcessId,
                runtimeInstanceIdentity.StartedAtUtc
            },
            Liveness = BuildSummary(liveness),
            Readiness = BuildSummary(readiness),
            Dependencies = BuildSummary(dependencies)
        });
    }

    private static object BuildSummary(HealthReport report)
    {
        return new
        {
            Status = report.Status.ToString(),
            Checks = report.Entries.Select(entry => new
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                DurationMs = entry.Value.Duration.TotalMilliseconds,
                Data = entry.Value.Data
            })
        };
    }
}
