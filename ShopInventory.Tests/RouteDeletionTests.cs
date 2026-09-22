using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Commands.DeleteRoute;
using ShopInventory.Features.VanSalesReports.Commands.SaveRoute;
using ShopInventory.Features.VanSalesReports.Commands.SaveRouteStop;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Deleting a route from the routes page.
/// </summary>
/// <remarks>
/// The delete keeps the row, and the cases that matter are the ones a hard delete would have got
/// wrong without anything looking broken: a seeded route put straight back by the next start, a
/// route's code held forever by a row nobody can see, and a van left on a route that no longer lists.
/// </remarks>
public sealed class RouteDeletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public RouteDeletionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_deleted_route_leaves_every_list_with_its_stops()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");

        var result = await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        Assert.False(result.IsError);

        var routes = await new GetRoutesHandler(_context).Handle(new GetRoutesQuery(true), default);
        Assert.DoesNotContain(routes.Value, route => route.Code == "EAST");
        Assert.Equal(9, routes.Value.Count);

        var stops = await new GetRouteStopsHandler(_context)
            .Handle(new GetRouteStopsQuery(null, IncludeInactive: true), default);
        Assert.DoesNotContain(stops.Value, stop => stop.RouteId == east.Id);

        _context.ChangeTracker.Clear();
        var row = await _context.Routes.SingleAsync(route => route.Id == east.Id);
        Assert.NotNull(row.DeletedAt);
        Assert.False(row.IsActive);
        Assert.False(await _context.RouteStops.AnyAsync(stop => stop.RouteId == east.Id && stop.IsActive));
    }

    /// <summary>
    /// The seeder matches on the seed key. A delete that removed the row would come back on the next
    /// deploy, stops and all, and nobody would know why.
    /// </summary>
    [Fact]
    public async Task The_next_start_does_not_put_a_deleted_seeded_route_back()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");
        await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        await SeedAsync();

        Assert.Equal(10, await _context.Routes.CountAsync());
        Assert.Equal(1, await _context.Routes.CountAsync(route => route.Code == "EAST"));
        Assert.False(await _context.RouteStops.AnyAsync(stop => stop.RouteId == east.Id && stop.IsActive));
    }

    [Fact]
    public async Task A_deleted_routes_code_is_free_for_a_new_route()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");
        await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        var created = await SaveHandler().Handle(
            new SaveRouteCommand(null, "EAST", "East Truck (new)", "Harare", null, null, null, null, true, null),
            default);

        Assert.False(created.IsError);
        Assert.NotEqual(east.Id, created.Value.Id);
    }

    [Fact]
    public async Task A_route_with_a_van_on_it_is_not_deleted()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");

        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "tinashe",
            PasswordHash = "not-a-real-hash",
            Role = "ADR",
            RouteId = east.Id
        });
        await _context.SaveChangesAsync();

        var result = await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);

        _context.ChangeTracker.Clear();
        Assert.Null((await RouteAsync("EAST")).DeletedAt);
    }

    [Fact]
    public async Task A_deleted_route_refuses_edits_and_new_stops()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");
        await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        var edit = await SaveHandler().Handle(
            new SaveRouteCommand(east.Id, "EAST", "East Truck", "Harare", null, null, null, null, true, null),
            default);
        var stop = await new SaveRouteStopHandler(_context, AuditSink(), NullLogger<SaveRouteStopHandler>.Instance)
            .Handle(new SaveRouteStopCommand(null, east.Id, "Msasa", DayOfWeek.Monday, null, 0, null, true, null), default);

        Assert.Equal(ErrorType.NotFound, edit.FirstError.Type);
        Assert.Equal(ErrorType.NotFound, stop.FirstError.Type);
    }

    [Fact]
    public async Task Deleting_twice_answers_success()
    {
        await SeedAsync();
        var east = await RouteAsync("EAST");

        await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);
        var again = await DeleteHandler().Handle(new DeleteRouteCommand(east.Id, null), default);

        Assert.False(again.IsError);
    }

    private Task SeedAsync()
    {
        _context.ChangeTracker.Clear();
        return DbInitializer.SeedVanSalesRoutesAsync(_context, NullLogger.Instance);
    }

    private Task<Models.Entities.RouteEntity> RouteAsync(string code)
        => _context.Routes.AsNoTracking().SingleAsync(route => route.Code == code && route.DeletedAt == null);

    private DeleteRouteHandler DeleteHandler()
        => new(_context, AuditSink(), NullLogger<DeleteRouteHandler>.Instance);

    private SaveRouteHandler SaveHandler()
        => new(_context, AuditSink(), NullLogger<SaveRouteHandler>.Instance);

    private static IAuditService AuditSink()
        => StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);
}
