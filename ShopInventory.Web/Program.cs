using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using ShopInventory.Web.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/shopinventory-web-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting ShopInventory Web Application");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // Add MudBlazor services
    builder.Services.AddMudServices();

    // Add Blazored LocalStorage
    builder.Services.AddBlazoredLocalStorage();

    // Add PostgreSQL Database for caching products
    builder.Services.AddDbContextFactory<WebAppDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

    // Add Cascading Authentication State (for Blazor component-level auth)
    // Note: We don't use server-side cookie auth - auth is handled by our custom AuthStateProvider
    // reading JWT tokens from localStorage. AuthorizeRouteView handles redirects for unauthenticated users.
    builder.Services.AddCascadingAuthenticationState();

    // Configure HttpClient for API calls with proper timeout and API key
    // Note: SAP queries can be slow, so we use a 120 second timeout
    var apiKey = builder.Configuration["ApiSettings:ApiKey"] ?? "";
    builder.Services.AddScoped(sp =>
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(builder.Configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5106/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
        return client;
    });

    // Add Authentication State Provider
    builder.Services.AddScoped<CustomAuthStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

    // Add Authentication services - configure to not redirect (Blazor handles auth via AuthorizeRouteView)
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "BlazorServer";
    }).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, BlazorServerAuthHandler>("BlazorServer", null);
    builder.Services.AddAuthorizationCore();

    builder.Services.AddMemoryCache();

    // Add application services
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IInvoiceService, InvoiceService>();
    builder.Services.AddScoped<IInventoryTransferCacheService, InventoryTransferCacheService>();
    builder.Services.AddScoped<IInventoryTransferService, InventoryTransferService>();
    builder.Services.AddScoped<IIncomingPaymentCacheService, IncomingPaymentCacheService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();
    builder.Services.AddScoped<IWarehouseStockCacheService, WarehouseStockCacheService>();
    builder.Services.AddScoped<IProductService, ProductService>();
    builder.Services.AddScoped<IPriceService, PriceService>();
    builder.Services.AddScoped<IBusinessPartnerService, BusinessPartnerService>();
    builder.Services.AddScoped<IMasterDataCacheService, MasterDataCacheService>();

    // Add audit and settings services
    builder.Services.AddScoped<IAuditService, AuditService>();
    builder.Services.AddScoped<IAppSettingsService, AppSettingsService>();

    // Add new feature services
    builder.Services.AddScoped<IReportService, ReportService>();
    builder.Services.AddScoped<IUserManagementService, UserManagementService>();
    builder.Services.AddScoped<INotificationClientService, NotificationClientService>();
    builder.Services.AddScoped<ISyncStatusClientService, SyncStatusClientService>();

    // Add Email service with MailKit
    builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
    builder.Services.AddScoped<IEmailService, EmailService>();

    // Add Theme, Localization, and Search services
    builder.Services.AddScoped<IThemeService, ThemeService>();
    builder.Services.AddScoped<ILocalizationService, LocalizationService>();

    // Add AI service
    builder.Services.Configure<AISettings>(builder.Configuration.GetSection("AI"));
    builder.Services.AddScoped<IAIService, AIService>();

    // Add background service to preload cache on startup
    builder.Services.AddHostedService<CachePreloadService>();

    var app = builder.Build();

    // Apply database migrations - recreate schema if needed
    using (var scope = app.Services.CreateScope())
    {
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WebAppDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // Check if all required tables exist, if not, recreate database
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            // Check for AuditLogs and AppSettings tables (newest additions)
            command.CommandText = @"SELECT COUNT(*) FROM information_schema.tables 
                                    WHERE table_name IN ('AuditLogs', 'AppSettings')";
            var result = await command.ExecuteScalarAsync();
            var tableCount = Convert.ToInt32(result);

            if (tableCount < 2) // Need both new tables
            {
                Log.Information("New tables detected in schema (AuditLogs, AppSettings), recreating database...");
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();
                Log.Information("Database recreated with new schema");
            }
            else
            {
                Log.Information("Database schema is up to date");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check database schema, attempting to create...");
            await dbContext.Database.EnsureCreatedAsync();
        }

        // Verify critical cache tables exist
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();
            using var verifyCmd = connection.CreateCommand();
            // PostgreSQL stores table names in lowercase in information_schema
            verifyCmd.CommandText = @"SELECT table_name FROM information_schema.tables 
                                      WHERE table_schema = 'public' 
                                      AND LOWER(table_name) IN ('cachedwarehousestocks', 'cachesyncinfo', 'cachedincomingpayments', 'cachedinventorytransfers')";
            var tableList = new List<string>();
            using (var reader = await verifyCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    tableList.Add(reader.GetString(0).ToLower());
                }
            }
            Log.Information("Cache tables found in database: {Tables}", string.Join(", ", tableList));

            if (!tableList.Contains("cachedwarehousestocks"))
            {
                Log.Warning("CachedWarehouseStocks table is MISSING! Creating it now...");
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""CachedWarehouseStocks"" (
                        ""Id"" SERIAL PRIMARY KEY,
                        ""ItemCode"" VARCHAR(50) NOT NULL,
                        ""ItemName"" VARCHAR(200),
                        ""BarCode"" VARCHAR(50),
                        ""WarehouseCode"" VARCHAR(20) NOT NULL,
                        ""InStock"" NUMERIC(18,6) NOT NULL DEFAULT 0,
                        ""Committed"" NUMERIC(18,6) NOT NULL DEFAULT 0,
                        ""Ordered"" NUMERIC(18,6) NOT NULL DEFAULT 0,
                        ""Available"" NUMERIC(18,6) NOT NULL DEFAULT 0,
                        ""UoM"" VARCHAR(20),
                        ""LastSyncedAt"" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CachedWarehouseStocks_WarehouseCode_ItemCode"" ON ""CachedWarehouseStocks"" (""WarehouseCode"", ""ItemCode"");
                    CREATE INDEX IF NOT EXISTS ""IX_CachedWarehouseStocks_WarehouseCode"" ON ""CachedWarehouseStocks"" (""WarehouseCode"");
                    CREATE INDEX IF NOT EXISTS ""IX_CachedWarehouseStocks_ItemCode"" ON ""CachedWarehouseStocks"" (""ItemCode"");
                ";
                await createCmd.ExecuteNonQueryAsync();
                Log.Information("CachedWarehouseStocks table created successfully");
            }

            if (!tableList.Contains("cachesyncinfo"))
            {
                Log.Warning("CacheSyncInfo table is MISSING! Creating it now...");
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""CacheSyncInfo"" (
                        ""CacheKey"" VARCHAR(50) PRIMARY KEY,
                        ""LastSyncedAt"" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""ItemCount"" INTEGER NOT NULL DEFAULT 0,
                        ""SyncSuccessful"" BOOLEAN NOT NULL DEFAULT FALSE,
                        ""LastError"" VARCHAR(500)
                    );
                ";
                await createCmd.ExecuteNonQueryAsync();
                Log.Information("CacheSyncInfo table created successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error verifying/creating cache tables: {Message}", ex.Message);
        }

        Log.Information("Database created/verified successfully");

        // Initialize default settings if not present
        try
        {
            var appSettingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await appSettingsService.InitializeDefaultSettingsAsync();
            Log.Information("Default settings initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not initialize default settings");
        }
    }

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    // Add Serilog request logging
    app.UseSerilogRequestLogging();

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
