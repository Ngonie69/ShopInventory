using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using ShopInventory.Web.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Middleware;
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
        .AddInteractiveServerComponents(options =>
        {
            // Enable detailed errors for debugging (configured in appsettings)
            options.DetailedErrors = builder.Configuration.GetValue<bool>("DetailedErrors", false);
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
    builder.Services.AddScoped<ISyncStatusClientService, SyncStatusClientService>();

    // Add Sales Order, Purchase Order, Credit Note, and Quotation services
    builder.Services.AddScoped<ISalesOrderService, SalesOrderService>();
    builder.Services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
    builder.Services.AddScoped<ICreditNoteService, CreditNoteService>();
    builder.Services.AddScoped<IQuotationService, QuotationService>();

    // Add System services (Exchange Rates, Backups, Webhooks)
    builder.Services.AddScoped<IExchangeRateService, ExchangeRateService>();
    builder.Services.AddScoped<IBackupService, BackupService>();
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
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        // Trust the reverse proxy - clear default restrictions
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    // Apply database migrations and seed default data
    using (var scope = app.Services.CreateScope())
    {
        await DatabaseInitializer.InitializeAsync(scope.ServiceProvider);
    }

    // Load cached application settings (CompanyName, DateFormat, etc.)
    await app.Services.GetRequiredService<IAppSettingsProvider>().ReloadAsync();

    // Configure the HTTP request pipeline.

    // ForwardedHeaders MUST be first - before any middleware that checks scheme/host/IP
    // This is critical for IIS behind a reverse proxy (e.g., load balancer terminating SSL)
    app.UseForwardedHeaders();

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
