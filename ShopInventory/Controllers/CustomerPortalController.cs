using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Controllers;

/// <summary>
/// Admin endpoints for managing customer portal users
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class CustomerPortalController : ControllerBase
{
    private readonly ILogger<CustomerPortalController> _logger;
    private readonly HttpClient _httpClient;

    public CustomerPortalController(
        ILogger<CustomerPortalController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    /// <summary>
    /// Register a new customer portal user
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(CustomerRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterCustomer([FromBody] CustomerRegistrationRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Validate password strength
            if (!IsPasswordStrong(request.Password))
            {
                return BadRequest(new { Message = "Password must be at least 8 characters and contain uppercase, lowercase, number, and special character" });
            }

            // Hash the password using BCrypt
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 12);

            var response = new CustomerRegistrationResponse
            {
                Success = true,
                CardCode = request.CardCode,
                Email = request.Email,
                PasswordHash = passwordHash,
                Message = "Customer registered successfully. Use the SQL below to insert into database.",
                SqlInsert = GenerateSqlInsert(request.CardCode, request.CardName, request.Email, passwordHash)
            };

            _logger.LogInformation("Generated registration for customer {CardCode}", request.CardCode);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering customer {CardCode}", request.CardCode);
            return StatusCode(500, new { Message = "An error occurred while registering the customer" });
        }
    }

    /// <summary>
    /// Generate password hash for a customer
    /// </summary>
    [HttpPost("generate-hash")]
    [ProducesResponseType(typeof(PasswordHashResponse), StatusCodes.Status200OK)]
    public IActionResult GeneratePasswordHash([FromBody] GenerateHashRequest request)
    {
        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { Message = "Password is required" });
        }

        if (!IsPasswordStrong(request.Password))
        {
            return BadRequest(new { Message = "Password must be at least 8 characters and contain uppercase, lowercase, number, and special character" });
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password, 12);

        return Ok(new PasswordHashResponse
        {
            PasswordHash = hash,
            Message = "Use this hash in the CustomerPortalUsers table"
        });
    }

    /// <summary>
    /// Bulk register customers from SAP Business Partners
    /// </summary>
    [HttpPost("bulk-register")]
    [ProducesResponseType(typeof(BulkRegistrationResponse), StatusCodes.Status200OK)]
    public IActionResult BulkRegisterCustomers([FromBody] BulkRegistrationRequest request)
    {
        var results = new List<CustomerRegistrationResponse>();
        var defaultPassword = request.DefaultPassword ?? "Welcome@123";

        if (!IsPasswordStrong(defaultPassword))
        {
            return BadRequest(new { Message = "Default password must be at least 8 characters and contain uppercase, lowercase, number, and special character" });
        }

        foreach (var customer in request.Customers)
        {
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword, 12);

            results.Add(new CustomerRegistrationResponse
            {
                Success = true,
                CardCode = customer.CardCode,
                Email = customer.Email,
                PasswordHash = passwordHash,
                SqlInsert = GenerateSqlInsert(customer.CardCode, customer.CardName, customer.Email, passwordHash)
            });
        }

        return Ok(new BulkRegistrationResponse
        {
            Success = true,
            Count = results.Count,
            DefaultPassword = defaultPassword,
            Message = $"Generated {results.Count} customer registrations",
            Customers = results,
            CombinedSql = string.Join("\n", results.Select(r => r.SqlInsert))
        });
    }

    private static bool IsPasswordStrong(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return false;

        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

        return hasUpper && hasLower && hasDigit && hasSpecial;
    }

    private static string GenerateSqlInsert(string cardCode, string cardName, string email, string passwordHash)
    {
        return $@"INSERT INTO ""CustomerPortalUsers"" 
    (""CardCode"", ""CardName"", ""Email"", ""PasswordHash"", ""IsActive"", ""TwoFactorEnabled"", ""EmailVerified"", ""ReceiveStatements"", ""CreatedAt"", ""UpdatedAt"")
VALUES 
    ('{EscapeSql(cardCode)}', '{EscapeSql(cardName)}', '{EscapeSql(email)}', '{passwordHash}', true, false, true, true, NOW(), NOW());";
    }

    private static string EscapeSql(string value)
    {
        return value?.Replace("'", "''") ?? "";
    }
}

#region DTOs

public class CustomerRegistrationRequest
{
    [Required]
    [StringLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string CardName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;
}

public class CustomerRegistrationResponse
{
    public bool Success { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SqlInsert { get; set; } = string.Empty;
}

public class GenerateHashRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;
}

public class PasswordHashResponse
{
    public string PasswordHash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class BulkRegistrationRequest
{
    public string? DefaultPassword { get; set; }
    public List<CustomerBasicInfo> Customers { get; set; } = new();
}

public class CustomerBasicInfo
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class BulkRegistrationResponse
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public string DefaultPassword { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<CustomerRegistrationResponse> Customers { get; set; } = new();
    public string CombinedSql { get; set; } = string.Empty;
}

#endregion
