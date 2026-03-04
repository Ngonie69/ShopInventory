# =============================================================================
# ShopInventory - Required Secrets Reference
# =============================================================================
# This file documents all secrets needed to run the application locally.
# NEVER put real values in appsettings.json — use one of these methods:
#
# METHOD 1: .NET User Secrets (RECOMMENDED for local dev)
#   cd ShopInventory && dotnet user-secrets set "KEY" "VALUE"
#   cd ShopInventory.Web && dotnet user-secrets set "KEY" "VALUE"
#
# METHOD 2: Environment Variables (for production/Docker)
#   Use double-underscore __ as section separator:
#   SAP__Password=secret  →  maps to SAP:Password in config
#
# METHOD 3: .env file with docker-compose (already supported)
#   Copy .env.example to .env and fill in values
# =============================================================================

# ─── API Project (ShopInventory) ─────────────────────────────────────────────
# cd ShopInventory

# Database
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=ShopInventory;Username=postgres;Password=YOUR_PG_PASSWORD"

# SAP B1 Service Layer
dotnet user-secrets set "SAP:Password" "YOUR_SAP_PASSWORD"

# JWT Token Signing (must be >= 32 characters for HS256)
dotnet user-secrets set "Jwt:SecretKey" "YOUR_JWT_SECRET_KEY_MIN_32_CHARS_LONG!"

# API Keys (must match what Web project sends)
dotnet user-secrets set "Security:ApiKeys:0:Key" "YOUR_MAIN_API_KEY"
dotnet user-secrets set "Security:ApiKeys:1:Key" "YOUR_TEST_API_KEY"


# ─── Web Project (ShopInventory.Web) ────────────────────────────────────────
# cd ShopInventory.Web

# Database
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=ShopInventoryWeb;Username=postgres;Password=YOUR_PG_PASSWORD"

# API Key (must match Security:ApiKeys:0:Key in API project)
dotnet user-secrets set "ApiSettings:ApiKey" "YOUR_MAIN_API_KEY"

# Email (optional, for statement emails)
# dotnet user-secrets set "Email:SmtpHost" "smtp.example.com"
# dotnet user-secrets set "Email:SmtpUsername" "your@email.com"
# dotnet user-secrets set "Email:SmtpPassword" "your_smtp_password"
# dotnet user-secrets set "Email:FromEmail" "noreply@yourcompany.com"


# ─── Verification ───────────────────────────────────────────────────────────
# List all stored secrets:
#   cd ShopInventory && dotnet user-secrets list
#   cd ShopInventory.Web && dotnet user-secrets list
#
# Secrets are stored at:
#   Windows: %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json
#   Linux:   ~/.microsoft/usersecrets/<UserSecretsId>/secrets.json
#
# User Secrets override appsettings.json automatically when
# ASPNETCORE_ENVIRONMENT=Development (the default for dotnet run).
# =============================================================================
