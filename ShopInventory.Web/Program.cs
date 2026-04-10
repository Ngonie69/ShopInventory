using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using ShopInventory.Web.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Middleware;
using ShopInventory.Web.Services;
using System.Net;
using System.IO.Compression;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/shopinventory-web-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31)
    .CreateLogger();

try
{
    Log.Information("Starting ShopInventory Web Application");

    // Clean up old IIS stdout log files (older than 31 days)
    try
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
        {
            var cutoff = DateTime.UtcNow.AddDays(-31);
            foreach (var file in Directory.GetFiles(logsDir, "stdout_*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to clean up old stdout log files");
    }

    var builder = WebApplication.CreateBuilder(args);

    var customerPortalJwtSecret = builder.Configuration["CustomerPortal:JwtSecret"] ?? builder.Configuration["Jwt:SecretKey"];
    if (string.IsNullOrWhiteSpace(customerPortalJwtSecret) ||
        customerPortalJwtSecret.StartsWith("${", StringComparison.Ordinal) ||
        customerPortalJwtSecret.Length < 32)
    {
        throw new InvalidOperationException(
            "Customer portal JWT secret is missing or invalid. Configure CustomerPortal:JwtSecret with a secret of at least 32 characters.");
    }

    // Use Serilog — read overrides from appsettings so production can further tune levels
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/shopinventory-web-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31));

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(options =>
        {
            // Enable detailed errors for debugging (configured in appsettings)
            options.DetailedErrors = builder.Configuration.GetValue<bool>("DetailedErrors", false);

            // Circuit retention limits to cap memory usage from disconnected browsers.
            // Default is 100 retained circuits / 3 min retention; tune for expected user count.
            options.DisconnectedCircuitMaxRetained = 200;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);

            // Max render batches the server will buffer while waiting for client acknowledgement.
            // Prevents runaway memory when a client falls behind.
            options.MaxBufferedUnacknowledgedRenderBatches = 10;
        })
        .AddHubOptions(options =>
        {
            // Increase SignalR message size to speed up file uploads (default 32KB is too small).
            // IBrowserFile.OpenReadStream reads data through SignalR in chunks of this size,
            // so a 5MB file at 32KB = ~160 round trips vs ~5 at 1MB.
            options.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB

            // Allow enough parallel invocations for bulk POD uploads (5 concurrent file reads + UI updates).
            options.MaximumParallelInvocationsPerClient = 10;
        });

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
    // Note: SAP queries can be very slow for large datasets, so we use a 5 minute timeout
    var apiKey = builder.Configuration["ApiSettings:ApiKey"] ?? "";
    var apiBaseUrl = builder.Configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5106/";

    // Use IHttpClientFactory for proper HttpClient lifecycle management
    builder.Services.AddHttpClient("ShopInventoryApi", client =>
    {
        client.BaseAddress = new Uri(apiBaseUrl);
        client.Timeout = TimeSpan.FromMinutes(5); // 5 minutes for slow SAP price list syncs
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
    });

    // Register a scoped HttpClient that uses the factory
    builder.Services.AddScoped(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return factory.CreateClient("ShopInventoryApi");
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

    // Add response compression for HTTP responses (static files, initial page load)
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.SmallestSize);

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
    builder.Services.AddSingleton<IAppSettingsProvider, AppSettingsProvider>();
    builder.Services.AddSingleton<IPrinterService, PrinterService>();

    // Add new feature services
    builder.Services.AddScoped<IReportService, ReportService>();
    builder.Services.AddScoped<IReportExportService, ReportExportService>();
    builder.Services.AddScoped<IUserManagementService, UserManagementService>();
    builder.Services.AddScoped<INotificationClientService, NotificationClientService>();
    builder.Services.AddScoped<INotificationHubService, NotificationHubService>();
    builder.Services.AddScoped<ISyncStatusClientService, SyncStatusClientService>();

    // Add Sales Order, Purchase Order, Credit Note, and Quotation services
    builder.Services.AddScoped<ISalesOrderService, SalesOrderService>();
    builder.Services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
    builder.Services.AddScoped<ICreditNoteService, CreditNoteService>();
    builder.Services.AddScoped<IQuotationService, QuotationService>();

    // Add Merchandiser service
    builder.Services.AddScoped<IMerchandiserService, MerchandiserService>();

    // Add System services (Exchange Rates, Backups, Webhooks)
    builder.Services.AddScoped<IExchangeRateService, ExchangeRateService>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddScoped<ISAPSettingsService, SAPSettingsService>();
    builder.Services.AddScoped<IWebhookService, WebhookService>();

    // Add Two-Factor Authentication service
    builder.Services.AddScoped<ITwoFactorWebService, TwoFactorWebService>();

    // Add Customer Portal services
    builder.Services.AddScoped<ICustomerLinkedAccountService, CustomerLinkedAccountService>();
    builder.Services.AddScoped<ICustomerAuthService, CustomerAuthService>();
    builder.Services.AddScoped<ICustomerStatementService, CustomerStatementService>();
    builder.Services.AddScoped<IPodService, PodService>();

    // Add Desktop Integration service (for viewing desktop app transactions)
    builder.Services.AddScoped<IDesktopIntegrationService, DesktopIntegrationService>();

    // Add REVMax fiscal device service
    builder.Services.AddScoped<IRevmaxService, RevmaxService>();

    // Add Email service with MailKit
    builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
    builder.Services.AddScoped<IEmailService, EmailService>();
    builder.Services.Configure<StatementEmailSettings>(builder.Configuration.GetSection("StatementEmails"));
    builder.Services.AddHostedService<StatementEmailScheduler>();

    // Add Theme, Localization, and Search services
    builder.Services.AddScoped<IThemeService, ThemeService>();
    builder.Services.AddScoped<ILocalizationService, LocalizationService>();

    // Add AI service
    builder.Services.Configure<AISettings>(builder.Configuration.GetSection("AI"));
    builder.Services.AddScoped<IAIService, AIService>();

    // Add background service to preload cache on startup
    builder.Services.AddHostedService<CachePreloadService>();

    // Configure forwarded headers for IIS behind reverse proxy
    // This ensures the app correctly detects HTTPS scheme and client IP
    // when behind a load balancer or reverse proxy that terminates SSL
    var knownReverseProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;

        foreach (var proxy in knownReverseProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }
    });

    var app = builder.Build();

    // Apply database migrations and seed default data
    using (var scope = app.Services.CreateScope())
    {
        await DatabaseInitializer.InitializeAsync(scope.ServiceProvider);
    }

    // Load cached application settings (CompanyName, DateFormat, etc.)
    try
    {
        await app.Services.GetRequiredService<IAppSettingsProvider>().ReloadAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not load cached application settings — will use defaults until database is available");
    }

    // Configure the HTTP request pipeline.

    // ForwardedHeaders MUST be first - before any middleware that checks scheme/host/IP
    // This is critical for IIS behind a reverse proxy (e.g., load balancer terminating SSL)
    app.UseForwardedHeaders();

    app.UseResponseCompression();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    // Swagger proxy - must be before security middleware to avoid interference
    app.UseSwaggerProxy();

    // Security middleware - order matters!
    app.UseSimpleRateLimit();         // Rate limiting first (DDoS protection)
    app.UseWebRequestValidation();    // Block malicious requests
    app.UseWebSecurityHeaders();      // Add security headers to all responses

    // Add Serilog request logging
    app.UseSerilogRequestLogging();

    // Only redirect to HTTPS if the request is actually HTTP
    // With ForwardedHeaders configured, this correctly detects the original scheme
    app.UseHttpsRedirection();

    app.UseStaticFiles();

    app.UseAntiforgery();

    app.MapStaticAssets();
    // AllowAnonymous at the endpoint level so the HTTP pipeline always renders the
    // Blazor HTML shell (App.razor) regardless of [Authorize] attributes on pages.
    // With prerender:false, no sensitive content is rendered during SSR anyway.
    // Actual authorization is enforced by AuthorizeRouteView + CustomAuthStateProvider
    // after the SignalR circuit connects and the JWT is read from localStorage.
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .AllowAnonymous();

    // Minimal API endpoint for backup file downloads.
    // The browser navigates here directly, so we use the factory-configured HttpClient
    // (which carries the API key) to stream the file from the backend API.
    app.MapGet("/download/backup/{id:int}", async (int id, IHttpClientFactory clientFactory, CancellationToken ct) =>
    {
        var client = clientFactory.CreateClient("ShopInventoryApi");

        var response = await client.GetAsync($"api/backup/{id}/download", HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? $"backup-{id}.bak";

        return Results.File(stream, contentType, fileName);
    });

    // Minimal API endpoint for POD file viewing/downloads.
    // Streams the file directly via HTTP, bypassing the SignalR connection
    // which cannot handle large binary payloads (images/PDFs).
    app.MapGet("/download/pod/{docEntry:int}/{attachmentId:int}", async (int docEntry, int attachmentId, IHttpClientFactory clientFactory, CancellationToken ct) =>
    {
        var client = clientFactory.CreateClient("ShopInventoryApi");

        var response = await client.GetAsync(
            $"api/invoice/{docEntry}/attachments/{attachmentId}/download",
            HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? $"pod-{attachmentId}";

        return Results.File(stream, contentType, fileName);
    });

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
