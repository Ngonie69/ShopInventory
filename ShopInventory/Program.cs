using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using ShopInventory.Authentication;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Middleware;
using ShopInventory.Services;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure PostgreSQL Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Configure Swagger with JWT authentication support
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Shop Inventory API",
        Version = "v1",
        Description = "A comprehensive inventory management API with SAP Business One integration for retail operations in Zimbabwe.",
        Contact = new OpenApiContact
        {
            Name = "Shop Inventory Support",
            Email = "support@shopinventory.co.zw"
        },
        License = new OpenApiLicense
        {
            Name = "Proprietary License"
        }
    });

    // Include XML comments for API documentation
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }

    // Add JWT authentication to Swagger
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token"
    });

    // Add API Key authentication to Swagger
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-API-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Enter your API Key"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });

    // Enable annotations
    c.EnableAnnotations();
});

// Configure settings
builder.Services.Configure<SAPSettings>(builder.Configuration.GetSection("SAP"));
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<RateLimitSettings>(builder.Configuration.GetSection("RateLimit"));
builder.Services.Configure<SecuritySettings>(builder.Configuration.GetSection("Security"));
builder.Services.Configure<RevmaxSettings>(builder.Configuration.GetSection("Revmax"));

// Get JWT settings for authentication configuration
var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException("JWT settings are not configured");

// Configure JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
        ClockSkew = TimeSpan.FromMinutes(1) // Reduced clock skew for tighter security
    };

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("JWT authentication failed: {Error}", context.Exception.Message);
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var username = context.Principal?.Identity?.Name;
            logger.LogDebug("Token validated for user: {Username}", username);
            return Task.CompletedTask;
        }
    };
})
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
    AuthenticationSchemes.ApiKey, options => { });

// Configure Authorization with policy supporting both JWT and API Key
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("ApiAccess", policy =>
        policy.RequireRole("Admin", "ApiUser", "User")
              .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthenticationSchemes.ApiKey));
});

// Configure Rate Limiting for DDoS protection
var rateLimitSettings = builder.Configuration.GetSection("RateLimit").Get<RateLimitSettings>()
    ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global rate limiting policy
    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.PermitLimit = rateLimitSettings.PermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimitSettings.WindowSeconds);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = rateLimitSettings.QueueLimit;
    });

    // Stricter rate limiting for authentication endpoints
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = rateLimitSettings.AuthEndpointPermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimitSettings.AuthEndpointWindowSeconds);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });

    // Sliding window for API endpoints
    options.AddSlidingWindowLimiter("api", opt =>
    {
        opt.PermitLimit = rateLimitSettings.PermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimitSettings.WindowSeconds);
        opt.SegmentsPerWindow = 4;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = rateLimitSettings.QueueLimit;
    });

    // Custom response for rate limit exceeded
    options.OnRejected = async (context, cancellationToken) =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("Rate limit exceeded for IP: {IpAddress}, Path: {Path}",
            context.HttpContext.Connection.RemoteIpAddress,
            context.HttpContext.Request.Path);

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers["Retry-After"] = rateLimitSettings.WindowSeconds.ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            message = "Too many requests. Please try again later.",
            retryAfter = rateLimitSettings.WindowSeconds
        }, cancellationToken);
    };
});

// Configure CORS
var securitySettings = builder.Configuration.GetSection("Security").Get<SecuritySettings>()
    ?? new SecuritySettings();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowConfiguredOrigins", policy =>
    {
        if (securitySettings.AllowedOrigins.Count > 0)
        {
            policy.WithOrigins(securitySettings.AllowedOrigins.ToArray())
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            // Default restrictive policy for development
            policy.WithOrigins("https://localhost:5001", "http://localhost:5000")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// Register authentication service
builder.Services.AddScoped<IAuthService, AuthService>();

// Register stock validation service - CRITICAL for preventing negative quantities
builder.Services.AddScoped<IStockValidationService, StockValidationService>();

// Register batch inventory validation service - CRITICAL for batch-managed items
// Implements FIFO/FEFO auto-allocation and prevents negative batch quantities
builder.Services.AddScoped<IBatchInventoryValidationService, BatchInventoryValidationService>();

// Register inventory lock service - Prevents race conditions during concurrent invoice posting
// For production with multiple instances, replace InMemoryInventoryLockService with Redis-based implementation
builder.Services.AddSingleton<IInventoryLockService, InMemoryInventoryLockService>();

// Register reporting service
builder.Services.AddScoped<IReportService, ReportService>();

// Register notification services
builder.Services.AddScoped<INotificationService, NotificationService>();

// Register email service
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, EmailService>();

// Register sync and queue services
builder.Services.AddScoped<IOfflineQueueService, OfflineQueueService>();
builder.Services.AddScoped<ISyncStatusService, SyncStatusService>();

// Register webhook service
builder.Services.AddScoped<IWebhookService, WebhookService>();

// Register payment gateway service
builder.Services.Configure<PaymentGatewaySettings>(builder.Configuration.GetSection("PaymentGateways"));
builder.Services.AddScoped<IPaymentGatewayService, PaymentGatewayService>();

// Register user management and security services
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddScoped<ITwoFactorService, TwoFactorService>();
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
builder.Services.AddScoped<IUserActivityService, UserActivityService>();

// Register statement service for PDF generation
builder.Services.AddScoped<IStatementService, StatementService>();
builder.Services.AddScoped<IIncomingPaymentService, IncomingPaymentService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IBusinessPartnerService, BusinessPartnerService>();
// Register email queue service for password reset
builder.Services.AddScoped<IEmailQueueService, EmailQueueService>();
// Register sales order and credit note services
builder.Services.AddScoped<ISalesOrderService, SalesOrderService>();
builder.Services.AddScoped<ICreditNoteService, CreditNoteService>();

// Register backup service
builder.Services.AddScoped<IBackupService, BackupService>();

// Register document management service
builder.Services.AddScoped<IDocumentService, DocumentService>();

// Register stock reservation service for desktop app integration
// This service manages stock reservations to prevent negative quantities
builder.Services.AddScoped<IStockReservationService, StockReservationService>();
builder.Services.AddScoped<IReservedQuantityProvider, ReservedQuantityProvider>();

// Register invoice queue service for batch posting to SAP
builder.Services.AddScoped<IInvoiceQueueService, InvoiceQueueService>();

// Register inventory transfer queue service for batch posting to SAP
builder.Services.AddScoped<IInventoryTransferQueueService, InventoryTransferQueueService>();

// Register background service for cleaning up expired reservations
builder.Services.AddHostedService<ReservationCleanupService>();

// Register background service for processing queued invoices
builder.Services.AddHostedService<InvoicePostingBackgroundService>();

// Register background service for processing queued inventory transfers
builder.Services.AddHostedService<InventoryTransferPostingBackgroundService>();

// Add permission-based authorization
builder.Services.AddPermissionAuthorization();

// Configure HttpClient for SAP Service Layer
builder.Services.AddHttpClient<ISAPServiceLayerClient, SAPServiceLayerClient>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var sapSettings = configuration.GetSection("SAP").Get<SAPSettings>();
    client.BaseAddress = new Uri(sapSettings?.ServiceLayerUrl ?? "https://10.10.10.6:50000/b1s/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(5); // Increased timeout for large data operations
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});

// Register exchange rate service (depends on SAP Service Layer client)
builder.Services.AddScoped<IExchangeRateService, ExchangeRateService>();

// Register REVMax fiscal integration client
// Typed HttpClient for REVMax API with retry policy
var revmaxSettings = builder.Configuration.GetSection("Revmax").Get<RevmaxSettings>()
    ?? new RevmaxSettings();

builder.Services.AddHttpClient<IRevmaxClient, RevmaxClient>((serviceProvider, client) =>
{
    client.BaseAddress = new Uri(revmaxSettings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(revmaxSettings.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Register fiscalization service - fiscalizes invoices after SAP posting
builder.Services.AddScoped<IFiscalizationService, FiscalizationService>();

var app = builder.Build();

// Initialize database and seed data
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var logger = services.GetRequiredService<ILogger<Program>>();
        await DbInitializer.InitializeAsync(context, logger);

        // Wire up the reserved quantity provider to the batch validation service
        // This is done after construction to avoid circular dependency
        var batchValidation = services.GetRequiredService<IBatchInventoryValidationService>();
        var reservedQtyProvider = services.GetRequiredService<IReservedQuantityProvider>();
        if (batchValidation is BatchInventoryValidationService batchService)
        {
            batchService.SetReservedQuantityProvider(reservedQtyProvider);
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while initializing the database");
        throw;
    }
}

// Security middleware - order matters!
app.UseRequestValidation(); // Validate requests first
app.UseSecurityHeaders();   // Add security headers

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

// Redirect root to Swagger
app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous();

// Enable HSTS in production
if (!app.Environment.IsDevelopment() && securitySettings.EnableHsts)
{
    app.UseHsts();
}

if (securitySettings.EnforceHttps)
{
    app.UseHttpsRedirection();
}

// Apply CORS
app.UseCors("AllowConfiguredOrigins");

// Apply rate limiting
app.UseRateLimiter();

// Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// Map controllers with default rate limiting
app.MapControllers().RequireRateLimiting("api");

app.Run();

