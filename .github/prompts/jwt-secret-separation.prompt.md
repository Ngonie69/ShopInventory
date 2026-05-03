---
description: "Audit and enforce separate JWT secrets for staff authentication vs customer portal across ShopInventory"
agent: "agent"
---

# Enforce Separate JWT Secrets — Staff vs Customer Portal

Audit and harden the JWT configuration to ensure staff and customer portal tokens use **completely independent signing secrets** with no fallback paths between them. Prioritize the work in this order: remove the secret fallback, add startup validation, confirm issuer and audience isolation, then update documentation and health-check or logging support.

## Current State

- **Staff tokens** (API): signed with `Jwt:SecretKey`, issued by `ShopInventoryAPI`, audience `ShopInventoryClients`. Generated in `ShopInventory/Services/AuthService.cs` → `GenerateAccessToken()`.
- **Customer portal tokens** (Web): signed with `CustomerPortal:JwtSecret`, issued by `ShopInventory.CustomerPortal`, audience `ShopInventory.Customers`. Generated in `ShopInventory.Web/Services/CustomerAuthService.cs` → `GenerateJwtToken()`.
- **Security gap**: `CustomerAuthService.GetRequiredCustomerPortalJwtSecret()` falls back to `Jwt:SecretKey` if `CustomerPortal:JwtSecret` is not set. This means a misconfigured deployment silently shares the same signing key for both token types.

## Requirements

1. **Remove the fallback** in `CustomerAuthService.GetRequiredCustomerPortalJwtSecret()`:
   - Read only `CustomerPortal:JwtSecret` — do NOT fall back to `Jwt:SecretKey`
   - Throw `InvalidOperationException` with this exact message if the secret is missing, starts with `${`, or is shorter than 32 characters: `CustomerPortal:JwtSecret is invalid: missing, placeholder, or shorter than 32 characters.`
   - This ensures deployments fail fast rather than silently sharing keys

2. **Add startup validation** in both projects' `Program.cs`:
   - **API**: Validate `Jwt:SecretKey` is present, ≥32 chars, and not a placeholder `${...}`
   - **Web**: Validate `CustomerPortal:JwtSecret` is present, ≥32 chars, and not a placeholder
   - Fail startup with a descriptive error if validation fails (don't silently proceed)

3. **Verify issuer/audience isolation**:
   - Staff token validation must reject tokens with issuer `ShopInventory.CustomerPortal`
   - Customer portal validation must reject tokens with issuer `ShopInventoryAPI`
   - Confirm `ValidateIssuer = true` and `ValidateAudience = true` in both validation paths

4. **Update configuration documentation**:
   - In `SECRETS.md`, ensure both secrets are documented with separate env var names
   - API: `JWT_SECRET_KEY` env var → `Jwt:SecretKey`
   - Web: `CUSTOMER_PORTAL_JWT_SECRET` env var → `CustomerPortal:JwtSecret`
   - Add a note that these MUST be different values

5. **Add a health check or startup log** confirming the two secrets are not identical (compare hashes, never log the actual values).

## Constraints

- Do not change token claims structure or expiration times.
- Do not modify the `JwtSettings` configuration model — only change resolution/validation logic.
- Use `ILogger<T>` for any startup validation logging.
- Never log actual secret values — only log whether validation passed/failed.
