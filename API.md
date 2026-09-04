# ShopInventory API Documentation

## Overview

| Property | Value |
|----------|-------|
| **Base URL** | `/api` |
| **Protocol** | HTTPS (enforced in production) |
| **Database** | PostgreSQL |
| **ERP Integration** | SAP Business One (Service Layer) |
| **Fiscal Integration** | ZIMRA FDMS via https://fiscal.kefaloscheese.com/ |
| **Payment Gateways** | PayNow, Innbucks, Ecocash |
| **API Format** | JSON |

## API Versioning

- Current default API version: `1.0`
- Base URL remains: `/api`
- If no API version is supplied, the server uses version `1.0`
- Clients can request a specific API version with the `X-API-Version` header or the `api-version` query string
- Breaking contract changes must be introduced in a new API version; existing version `1.0` endpoints remain supported for current clients

Examples:

- `GET /api/Health`
- `GET /api/Health?api-version=1.0`
- `X-API-Version: 1.0`

---

## Table of Contents

- [Authentication](#authentication)
- [Authorization & Permissions](#authorization--permissions)
- [Rate Limiting](#rate-limiting)
- [Security Headers & Middleware](#security-headers--middleware)
- [Idempotency](#idempotency)
- [Common Response Patterns](#common-response-patterns)
- [Endpoints](#endpoints)
  - [Auth](#1-auth)
  - [Password Management](#2-password-management)
  - [Two-Factor Authentication](#3-two-factor-authentication)
  - [Users](#4-users)
  - [User Management](#5-user-management)
  - [User Activity](#6-user-activity)
  - [Products](#7-products)
  - [Stock](#8-stock)
  - [Prices](#9-prices)
  - [Invoices](#10-invoices)
  - [Credit Notes](#11-credit-notes)
  - [Sales Orders](#12-sales-orders)
  - [Quotations](#13-quotations)
  - [Purchase Orders](#14-purchase-orders)
  - [Incoming Payments](#15-incoming-payments)
  - [Payment Gateways](#16-payment-gateways)
  - [Inventory Transfers](#17-inventory-transfers)
  - [Business Partners](#18-business-partners)
  - [Exchange Rates](#19-exchange-rates)
  - [GL Accounts](#20-gl-accounts)
  - [Cost Centres](#21-cost-centres)
  - [Documents](#22-documents)
  - [Reports](#23-reports)
  - [Item Volume Conversions](#23a-item-volume-conversions)
  - [Statements](#24-statements)
  - [Notifications](#25-notifications)
  - [Webhooks](#26-webhooks)
  - [Backups](#27-backups)
  - [Rate Limit Management](#28-rate-limit-management)
  - [SAP Settings](#29-sap-settings)
  - [Desktop Integration](#30-desktop-integration)
  - [Customer Portal](#31-customer-portal)
  - [Fiscalisation](#32-fiscalisation)
  - [Health](#33-health)
  - [Van Sales](#34-van-sales)
  - [Timesheets](#35-timesheets)
  - [Route Customers](#36-route-customers)
  - [Crates](#37-crates)
  - [Merchandiser](#38-merchandiser)
  - [Sync & SAP Connection](#39-sync--sap-connection)
  - [WhatsApp](#40-whatsapp)
  - [Email](#41-email)
  - [Push Notifications](#42-push-notifications)
  - [Exception Center](#43-exception-center)
  - [Approval Process](#44-approval-process)
  - [Fiscal Device Offline Leases](#45-fiscal-device-offline-leases)
  - [Batches](#46-batches)
  - [App Version](#47-app-version)
  - [Purchasing Documents](#48-purchasing-documents)
  - [Van Sales Customer Ordering](#49-van-sales-customer-ordering)
  - [Credit Note Approvals (SAP)](#50-credit-note-approvals-sap)
  - [Shops](#51-shops)
- [DTOs Reference](#dtos-reference)

---

## Authentication

The API supports two authentication methods:

### JWT Bearer Token

Include the token in the `Authorization` header:

```
Authorization: Bearer <access_token>
```

| Setting | Value |
|---------|-------|
| Issuer | `ShopInventoryAPI` |
| Audience | `ShopInventoryClients` |
| Access Token TTL | 60 minutes |
| Refresh Token TTL | 7 days |

**Login Flow:**

1. `POST /api/Auth/login` with username/password
2. If 2FA is enabled, response includes `RequiresTwoFactor: true` and a `TwoFactorToken`
3. Re-submit login with the 2FA code and token
4. On success, receive `AccessToken` + `RefreshToken`
5. Use `POST /api/Auth/refresh` before the access token expires

### API Key

Include the key in the `X-API-Key` header:

```
X-API-Key: <api_key>
```

API keys are configured server-side with assigned roles and optional expiration dates.

---

## Authorization & Permissions

### Roles

Users are assigned one role: `Admin`, `Manager`, `User`, `Cashier`, `StockController`, `DepotController`, `PodOperator`, or `ApiUser`.

### Policies

| Policy | Required Roles |
|--------|---------------|
| `AdminOnly` | Admin |
| `ApiAccess` | Admin, ApiUser, User, Cashier, StockController, DepotController, Manager, PodOperator |

### Fine-Grained Permissions

Endpoints may require specific permissions checked via the `[RequirePermission]` attribute:

| Category | Permissions |
|----------|-------------|
| **Dashboard** | `dashboard.view` |
| **Products** | `products.view`, `products.create`, `products.edit`, `products.delete`, `products.manage_prices` |
| **Invoices** | `invoices.view`, `invoices.create`, `invoices.edit`, `invoices.delete`, `invoices.void` |
| **Sales Quotations** | `quotations.view`, `quotations.create`, `quotations.edit`, `quotations.delete` |
| **Credit Note Approvals** | `creditnotes.approve`, `creditnotes.add_approved` |
| **Purchasing** | `purchasing.view`, `purchasing.create`, `purchasing.edit`, `purchasing.delete`, `purchasing.approve`, `purchasing.receive` |
| **Payments** | `payments.view`, `payments.create`, `payments.refund`, `payments.process_refunds` |
| **Inventory** | `stock.view`, `stock.edit`, `stock.transfer`, `stock.adjust`, `inventory.view`, `inventory.transfer`, `inventory.adjust` |
| **Reports** | `reports.view`, `reports.export` |
| **Customers** | `customers.view`, `customers.create`, `customers.edit`, `customers.delete` |
| **Users** | `users.view`, `users.create`, `users.edit`, `users.delete`, `users.manage_roles`, `users.manage_permissions` |
| **Settings** | `settings.view`, `settings.edit`, `settings.manage`, `settings.integrations` |
| **Audit** | `audit.view`, `audit.export` |
| **Webhooks** | `webhooks.view`, `webhooks.manage` |
| **System** | `sync.view`, `sync.manage`, `system.admin`, `backups.view`, `backups.create`, `backups.restore`, `backups.delete` |

---

## Rate Limiting

| Scope | Limit | Window |
|-------|-------|--------|
| Global | 100 requests | 60 seconds |
| Auth endpoints | 10 requests | 60 seconds |
| Queue limit | 10 | — |

When rate-limited, the API returns **HTTP 429 Too Many Requests** with a `Retry-After` header.

---

## Security Headers & Middleware

All responses include:

| Header | Value |
|--------|-------|
| `X-Frame-Options` | `DENY` |
| `X-Content-Type-Options` | `nosniff` |
| `X-XSS-Protection` | `1; mode=block` |
| `Content-Security-Policy` | Strict (relaxed for Swagger UI) |
| `Cache-Control` | `no-store, no-cache` for mutating requests; `public, max-age=300` for GET requests |

---

## Idempotency

For POST/PUT operations on critical endpoints, include an `Idempotency-Key` header to prevent duplicate submissions:

```
Idempotency-Key: <unique-uuid>
```

**Supported endpoints:**
- `/api/Invoice`
- `/api/SalesOrder`
- `/api/CreditNote`
- `/api/IncomingPayment`
- `/api/InventoryTransfer`
- `/api/Payment`

Idempotency keys expire after **60 minutes**.

### Recovering an order from its key

A replayed key does **not** return the original document. Within the 60 minute window the
middleware answers a repeat of the same `Idempotency-Key` with the first request's status code and a
bare `{ "message": ... }` body, and after the window expires the request reaches the handler, where
sales orders are deduplicated again on the persisted `clientRequestId`. Either way a client that
lost the original response — a timeout, a dropped connection, a process kill — cannot learn the
order number from a retry.

For mobile sales orders, ask instead:

```
GET /api/Merchandiser/mobile/orders/by-client-request/{clientRequestId}
```

Returns the caller's own mobile order created under that key, or **404** when no order exists yet —
which is the server confirming the request is still safe to send. A client must not read a transport
failure on this call as a 404.

### Endpoints that replay the real document

Crate POD upload and crate GRV creation do their own durable idempotency in the handler, which
persists the response and replays the actual document on a repeated key:

| Endpoint | Key carrier |
| --- | --- |
| `POST /api/crates/transactions/{id}/pods` | `clientRequestId` form field (or `Idempotency-Key` header) |
| `POST /api/crates/transactions/{id}/grvs` | `clientRequestId` form field (or `Idempotency-Key` header) |

`IdempotencyMiddleware` deliberately stands aside for these two routes, because its own in-memory
replay would short-circuit the request and answer with a bare message instead of the document.

The key must stay stable across retries of one submission and be retired once it succeeds or once
the submission's content changes — reusing a key with a different payload returns **409
`Idempotency.RequestMismatch`**. Only successful submissions are recorded, so a refusal (negative
quantity, no variance, missing merchandiser POD) stays retryable once the cause is fixed.

---

## Common Response Patterns

### Paginated List Response

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 150,
  "totalPages": 8,
  "hasMore": true,
  "items": [ ... ]
}
```

### Error Response

```json
{
  "error": "Description of what went wrong",
  "details": "Additional context (optional)"
}
```

### Validation Error (400)

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "errors": {
    "FieldName": ["Error message"]
  }
}
```

---

## Endpoints

### 1. Auth

**Base route:** `/api/Auth`

#### POST `/api/Auth/login`

Login with username and password.

- **Auth:** None (Anonymous)
- **Request Body:**

```json
{
  "username": "string",
  "password": "string"
}
```

- **Response (200):**

```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "abc123...",
  "expiresAt": "2026-04-01T13:00:00Z",
  "tokenType": "Bearer",
  "user": {
    "username": "admin",
    "role": "Admin",
    "email": "admin@example.com",
    "assignedWarehouseCode": "WH01",
    "assignedWarehouseCodes": ["WH01", "WH02"]
  }
}
```

- **Response (2FA required):**

```json
{
  "requiresTwoFactor": true,
  "twoFactorToken": "temp-token..."
}
```

No `accessToken` comes back with that answer. The challenge token is not a session and authorises
nothing except the second step below.

#### POST `/api/Auth/login/two-factor`

Finish a login that came back `requiresTwoFactor`.

- **Auth:** None (Anonymous). Rate limited under the `auth` policy, same as `login`.
- **Request Body:**

```json
{
  "twoFactorToken": "temp-token...",
  "code": "123456",
  "isBackupCode": false
}
```

- **Response (200):** the ordinary login response — access token, refresh token and user.

`code` is the six-digit TOTP from the authenticator app, or one of the backup codes issued by
`/api/TwoFactor/enable` when `isBackupCode` is `true`. A backup code is spent on use.

A bad code, a spent backup code, an expired challenge token and a token that never existed are
**one refusal**, the same `Auth.InvalidCredentials` a wrong password gets. Separating them would let
the endpoint confirm that a username and password were right, which is exactly what the second
factor exists to stop being enough.

Both outcomes are audited: success as a login naming the user, failure as `LoginFailed` against
`"Unknown"` — the challenge token is not resolved to a user before it has been proved, so a failed
attempt cannot name the account it was aimed at.

#### POST `/api/Auth/refresh`

Exchange a refresh token for a new access token.

- **Auth:** None (Anonymous)
- **Request Body:**

```json
{
  "refreshToken": "abc123..."
}
```

- **Response (200):** Same as login response.

#### POST `/api/Auth/logout`

Revoke the current refresh token.

- **Auth:** Bearer
- **Request Body:**

```json
{
  "refreshToken": "abc123..."
}
```

#### GET `/api/Auth/me`

Get the current authenticated user's info.

- **Auth:** Bearer
- **Response (200):** `UserInfo` object.

#### POST `/api/Auth/register`

Register a new user (Admin only).

- **Auth:** Bearer + Admin role
- **Request Body:**

```json
{
  "username": "string",
  "email": "string",
  "password": "string",
  "role": "string"
}
```

#### Passkeys

WebAuthn, in the usual two-step shape: ask for options, then send back what the authenticator
signed. Registration needs a session; login cannot have one.

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/Auth/passkeys` | Bearer | The caller's registered passkeys |
| POST | `/api/Auth/passkeys/register/options` | Bearer | Begin registering one |
| POST | `/api/Auth/passkeys/register/complete` | Bearer | Finish registering it |
| POST | `/api/Auth/passkeys/login/options` | **anonymous** | Begin a passkey login |
| POST | `/api/Auth/passkeys/login/complete` | **anonymous** | Finish it — answers the same token pair as `/login` |

#### Mobile biometrics

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| POST | `/api/Auth/mobile/biometric-login` | **anonymous** | Exchange a stored biometric credential for tokens |
| POST | `/api/Auth/mobile/biometric-preference` | Bearer | Record that this device may use biometrics |

The pair splits the way passkeys do: recording the preference is something a signed-in user does,
logging in with it is by definition something they cannot.

---

### 2. Password Management

**Base route:** `/api/Password`

#### POST `/api/Password/reset/request`

Request a password reset email.

- **Auth:** None (Anonymous)
- **Request Body:**

```json
{
  "email": "user@example.com"
}
```

#### GET `/api/Password/reset/validate?token={token}`

Validate a password reset token.

- **Auth:** None (Anonymous)

#### POST `/api/Password/reset/complete`

Complete the password reset.

- **Auth:** None (Anonymous)
- **Request Body:**

```json
{
  "token": "string",
  "newPassword": "string",
  "confirmPassword": "string"
}
```

#### POST `/api/Password/change`

Change the current user's password.

- **Auth:** Bearer
- **Request Body:**

```json
{
  "currentPassword": "string",
  "newPassword": "string",
  "confirmPassword": "string"
}
```

#### Credentials

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/Password/credentials` | Bearer | The caller's sign-in credentials |
| PUT | `/api/Password/credentials` | Bearer | Update them |

---

### 3. Two-Factor Authentication

**Base route:** `/api/TwoFactor`

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/TwoFactor/status` | Get current 2FA status for the authenticated user |
| POST | `/api/TwoFactor/setup` | Initiate 2FA setup, returns secret key + QR code URI |
| POST | `/api/TwoFactor/enable` | Verify the first code, turn 2FA on, and issue the backup codes |
| POST | `/api/TwoFactor/verify` | Verify a TOTP or backup code for the signed-in user |
| POST | `/api/TwoFactor/backup-codes/regenerate` | Replace the backup codes with a fresh set |
| POST | `/api/TwoFactor/disable` | Disable 2FA |

**All endpoints require Bearer authentication**, and every one of them acts on the caller's own
account — none takes a user id, so 2FA cannot be set up or removed for somebody else here.

##### Turning it on is two calls

`setup` generates a secret, stores it against the user as **pending**, and returns it. It does not
turn 2FA on: `enable` does, after the first code proves the authenticator was actually enrolled.
Storing the secret without arming the account is what lets a setup be abandoned — closing the tab
after scanning leaves the account exactly as it was.

**Setup Response:**

```json
{
  "secretKey": "BASE32SECRET",
  "qrCodeUri": "otpauth://totp/ShopInventory:user?secret=...",
  "manualEntryKey": "XXXX XXXX XXXX XXXX",
  "backupCodes": []
}
```

`backupCodes` is **always empty here**. The codes are generated by `enable` and shown once, in its
response. Running `setup` again on an account mid-enrolment issues a new secret and clears any codes
already stored; on an account where 2FA is already on it is refused — disable first.

##### POST `/api/TwoFactor/enable`

**Body:** `{ "code": "123456" }` — the first TOTP from the app now holding the pending secret.

**Response (200):**

```json
{
  "message": "Two-factor authentication enabled successfully",
  "backupCodes": ["A1B2-C3D4", "…"]
}
```

Ten codes, each eight hex characters as `XXXX-XXXX`, stored hashed. **This is the only time they are
readable** — no endpoint reads them back, so an operator who loses them regenerates rather than
recovers. `400` if setup was never started, if 2FA is already on, or if the code does not verify.

##### POST `/api/TwoFactor/backup-codes/regenerate`

**Body:** `{ "code": "123456" }` — a current TOTP, not a backup code.

**Response (200):** the same shape as `enable`, carrying ten fresh codes.

Replaces the whole set: every previously issued code stops working, which is the point when a
printed list has gone astray. Requires 2FA to be **on** — an account still mid-setup has no codes to
replace and is refused. Proving possession of the authenticator is required precisely because
someone holding a stolen backup code must not be able to mint themselves ten more.

---

### 4. Users

**Base route:** `/api/User`  
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/User` | List all users (paginated, searchable) |
| GET | `/api/User/{id}` | Get user by ID |
| GET | `/api/User/roles` | The roles a user can be given |
| POST | `/api/User` | Create a user |
| PUT | `/api/User/{id}` | Update user details |
| DELETE | `/api/User/{id}` | Delete a user |
| POST | `/api/User/{id}/change-password` | Admin-initiated password change |
| POST | `/api/User/{id}/unlock` | Unlock a locked-out account |
| POST | `/api/User/{id}/deactivate` | Deactivate an account without deleting it |
| POST | `/api/User/{id}/activate` | Reinstate one |

**GET list query parameters:** `page` (1), `pageSize` (20), `search`, `role`

Deactivating is not deleting: the account stays, its history stays, and it can be reinstated.
[User Management](#5-user-management) does the same job under fine-grained permissions rather than
the `AdminOnly` policy this controller sits behind — prefer it unless you specifically want the
admin-only surface.

**User DTO:**

```json
{
  "id": 1,
  "username": "jdoe",
  "email": "jdoe@example.com",
  "role": "Cashier",
  "firstName": "John",
  "lastName": "Doe",
  "isActive": true,
  "emailVerified": true,
  "failedLoginAttempts": 0,
  "lockoutEnd": null,
  "createdAt": "2026-01-01T00:00:00Z",
  "lastLoginAt": "2026-04-01T08:30:00Z",
  "assignedWarehouseCodes": ["WH01"]
}
```

---

### 5. User Management

**Base route:** `/api/UserManagement`  
**Auth:** Bearer + specific permissions per endpoint

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/UserManagement` | `users.view` | List users with full details |
| GET | `/api/UserManagement/{id}` | `users.view` | Get user with permissions |
| POST | `/api/UserManagement` | `users.create` **or** `users.create_merchandiser_accounts` | Create user with granular permissions |
| PUT | `/api/UserManagement/{id}` | `users.edit` | Update user + permissions |
| DELETE | `/api/UserManagement/{id}` | `users.delete` | Delete user |
| GET | `/api/UserManagement/{id}/permissions` | `users.view` | One user's permissions |
| PUT | `/api/UserManagement/{id}/permissions` | `users.manage_permissions` | Replace them |
| GET | `/api/UserManagement/permissions/available` | `users.view` | Every permission that can be granted |
| POST | `/api/UserManagement/{id}/unlock` | `users.edit` | Unlock a locked-out account |
| POST | `/api/UserManagement/{id}/reset-2fa` | `users.edit` | Clear a user's 2FA enrolment |
| GET | `/api/UserManagement/merchandisers` | `users.create_merchandiser_accounts` | The merchandiser accounts the caller manages |
| PUT | `/api/UserManagement/merchandisers/{id}/assigned-customers` | `users.create_merchandiser_accounts` | Set one merchandiser's customers |
| PUT | `/api/UserManagement/drivers/assigned-customers` | `users.edit` | Set the drivers' customers globally |
| GET | `/api/UserManagement/me` | authenticated | The caller |
| GET | `/api/UserManagement/me/permissions` | authenticated | The caller's own permissions |

**Query parameters:** `page` (1), `pageSize` (**10**, not 20), `search`, `role`, `isActive`

`POST /api/UserManagement` takes **either** `users.create` or
`users.create_merchandiser_accounts` — that is what lets a supervisor create merchandiser accounts
without holding general user-creation rights. The two `me` routes need no permission at all: a user
can always ask what they are allowed to do.

**Create User Request:**

```json
{
  "username": "string",
  "email": "string",
  "password": "string",
  "firstName": "string",
  "lastName": "string",
  "role": "Cashier",
  "permissions": ["invoices.view", "invoices.create", "payments.view"],
  "assignedWarehouseCodes": ["WH01", "WH02"],
  "sendWelcomeEmail": true
}
```

**User Detail Response:**

```json
{
  "id": 1,
  "username": "jdoe",
  "email": "jdoe@example.com",
  "firstName": "John",
  "lastName": "Doe",
  "role": "Cashier",
  "isActive": true,
  "emailVerified": true,
  "twoFactorEnabled": false,
  "isLockedOut": false,
  "lockoutEnd": null,
  "permissions": ["invoices.view", "invoices.create"],
  "assignedWarehouseCodes": ["WH01"],
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-03-15T10:00:00Z",
  "lastLoginAt": "2026-04-01T08:30:00Z"
}
```

---

### 6. User Activity

**Base route:** `/api/UserActivity`

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/UserActivity` | `audit.view` | The audit log itself, paged and filtered |
| GET | `/api/UserActivity/dashboard` | `audit.view` | System-wide activity dashboard |
| GET | `/api/UserActivity/user/{userId}` | `audit.view` | Specific user's activity summary |
| GET | `/api/UserActivity/me` | ApiAccess | Current user's own activity |
| GET | `/api/UserActivity/filter-options` | `audit.view` | The usernames and actions present in the log |
| GET | `/api/UserActivity/entity/{entityType}/{entityId}` | `audit.view` | Everything recorded against one record |

`me` is the only one of these that is not gated on `audit.view`: reading your own trail is not
reading the audit log.

##### GET `/api/UserActivity`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `page` | `1` | |
| `pageSize` | `50` | |
| `userId` | — | A user's GUID |
| `username` | — | |
| `action` | — | |
| `entityType` | — | |
| `startDate` / `endDate` | — | Filter on the entry timestamp |

##### GET `/api/UserActivity/filter-options`

Takes the same optional `startDate` / `endDate`, and answers with the values actually present in the
audit log over that window:

```json
{
  "users": ["admin", "kmoyo"],
  "actions": ["Login", "LoginFailed", "InvoiceCreated"]
}
```

Both lists are distinct and sorted, and they are read from the log rather than from the user table
or an enum — so they name who and what is really there, including accounts since deleted and actions
no longer raised. That is what makes them safe to render as a filter: every option returns rows.
Narrow the dates and the lists narrow with them.

##### GET `/api/UserActivity/entity/{entityType}/{entityId}`

Every audit entry recorded against one record, for the history panel on that record's page.
`entityType` is the string the writer recorded — `"User"`, `"Invoice"` — and `entityId` is free text
rather than a GUID route constraint, because not every audited entity is keyed on one: a SAP
document is keyed on its DocEntry, and a shop on its code.

**Dashboard Response:**

```json
{
  "fromDate": "2026-03-01",
  "toDate": "2026-04-01",
  "totalUsers": 25,
  "activeUsers": 18,
  "totalLogins": 450,
  "failedLogins": 12,
  "totalActions": 3200,
  "activityByUser": [...],
  "activityByType": [...],
  "hourlyActivity": [...]
}
```

---

### 7. Products

**Base route:** `/api/Product`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Product` | Get all products from SAP |
| GET | `/api/Product/groups` | SAP's item groups, so a group code can be shown as a name |
| GET | `/api/Product/van-sale-catalogue` | Every van-sale item — see [Van Sales](#34-van-sales) |
| GET | `/api/Product/warehouse/{warehouseCode}` | Products in a specific warehouse with batch info |
| GET | `/api/Product/warehouse/{warehouseCode}/paged` | The same, paginated (`page`, `pageSize`, `businessPartnerCode`, `priceListNum`, `vanSaleOnly`, `cursor`, `after`) |
| GET | `/api/Product/warehouse/{warehouseCode}/item/{itemCode}/batches` | Batch information for one item in one warehouse |
| GET | `/api/Product/{itemCode}` | Get a single product |

Batches are per item **and** warehouse — there is no route that gives an item's batches across
every warehouse.

**Product DTO:**

```json
{
  "itemCode": "PRD001",
  "itemName": "Widget A",
  "barCode": "1234567890",
  "itemType": "itItems",
  "managesBatches": true,
  "quantityInStock": 150.0,
  "quantityAvailable": 120.0,
  "quantityCommitted": 30.0,
  "price": 25.99,
  "defaultWarehouse": "WH01",
  "uoM": "Each",
  "batches": [
    {
      "batchNumber": "B2026-001",
      "quantity": 80.0,
      "status": "Released",
      "expiryDate": "2027-06-01",
      "admissionDate": "2026-01-15"
    }
  ]
}
```

---

### 8. Stock

**Base route:** `/api/Stock`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Stock/warehouses` | Get all warehouses (cached 5 min) |
| GET | `/api/Stock/warehouse-codes` | Get just warehouse codes |
| GET | `/api/Stock/warehouse/{warehouseCode}` | Get all stock in a specific warehouse |
| GET | `/api/Stock/warehouse/{warehouseCode}/paged` | The same, paginated (`page`, `pageSize`) |
| GET | `/api/Stock/warehouse/{warehouseCode}/items` | Stock for named items only — `itemCodes` is a comma-separated list |
| GET | `/api/Stock/warehouse/{warehouseCode}/sales` | Sales out of a warehouse (`fromDate`, `toDate`) |
| POST | `/api/Stock/warehouse/{warehouseCode}/sales` | The same query with `fromDate` and `toDate` in the body |

There is no batch route on this controller. Batch detail is
`GET /api/Product/warehouse/{warehouseCode}/item/{itemCode}/batches`.

**Warehouse DTO:**

```json
{
  "warehouseCode": "WH01",
  "warehouseName": "Main Warehouse",
  "location": "Harare",
  "street": "123 Industrial Rd",
  "city": "Harare",
  "country": "ZW",
  "isActive": true
}
```

**Stock Quantity DTO:**

```json
{
  "itemCode": "PRD001",
  "itemName": "Widget A",
  "barCode": "1234567890",
  "warehouseCode": "WH01",
  "inStock": 150.0,
  "committed": 30.0,
  "ordered": 50.0,
  "available": 120.0,
  "uoM": "Each",
  "packagingCode": "PKG001",
  "packagingMaterialStock": 500,
  "packagingLabelStock": 1200,
  "packagingLidStock": 800
}
```

---

### 9. Prices

**Base route:** `/api/Price`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Price/cached` | Get cached prices (synced every 5 minutes) |
| GET | `/api/Price` | Get all prices directly from SAP |
| GET | `/api/Price/grouped` | Prices grouped by item |
| GET | `/api/Price/{itemCode}` | Prices for one item |
| GET | `/api/Price/currency/{currency}` | Prices in one currency |
| GET | `/api/Price/businesspartner/{cardCode}` | Customer-specific pricing |
| GET | `/api/Price/pricelists` | The price lists |
| GET | `/api/Price/pricelists/{priceListNum}/items` | Items on one price list |
| GET | `/api/Price/pricelists/{priceListNum}/items/{itemCode}` | One item's price on one list |
| POST | `/api/Price/sync` | Force a price sync from SAP |
| POST | `/api/Price/pricelists/sync` | Sync the price lists |
| POST | `/api/Price/pricelists/{priceListNum}/sync` | Sync one price list |

Every route here takes `ApiAccess` and nothing more — the sync routes are **not** Admin-only, and a
full `/api/Price/sync` runs under a 30-minute timeout.

**Price DTO:**

```json
{
  "itemCode": "PRD001",
  "itemName": "Widget A",
  "price": 25.99,
  "currency": "USD",
  "priceListNum": 1,
  "priceListName": "Base Price List"
}
```

---

### 10. Invoices

**Base route:** `/api/Invoice`  
**Auth:** Bearer + **roles** as noted

This controller gates on `[Authorize(Roles = …)]`, not on `[RequirePermission]`. The
`invoices.view` / `invoices.edit` / `invoices.delete` permission constants exist, but nothing on
this controller reads them — granting one to a user changes nothing here.

| Method | Endpoint | Roles | Description |
|--------|----------|-------|-------------|
| POST | `/api/Invoice` | Admin, Cashier | Create a new invoice (posts to SAP) |
| POST | `/api/Invoice/validate` | Admin, Cashier | Validate an invoice before posting |
| GET | `/api/Invoice/paged` | Admin, Cashier, StockController, Manager | List invoices, paginated |
| GET | `/api/Invoice/{docEntry}` | Admin, Cashier, StockController, Manager | Get invoice by SAP DocEntry |
| GET | `/api/Invoice/by-docnum/{docNum}` | Admin, Cashier, StockController, Manager, Driver, PodOperator, Operator, ApiUser | Get invoice by SAP DocNum |
| GET | `/api/Invoice/date-range` | Admin, Cashier, StockController, Manager | Get invoices by date range (`fromDate`, `toDate`, `page`, `pageSize`) |
| GET | `/api/Invoice/open` | Admin, Cashier, StockController, Manager, PodOperator | Open invoices |
| GET | `/api/Invoice/customer/{cardCode}` | Admin, Cashier, StockController, Manager, Driver, PodOperator | A customer's invoices |
| GET | `/api/Invoice/{docEntry}/pdf` | Admin, Cashier, StockController, Manager | Download the invoice as a PDF |
| GET | `/api/Invoice/{itemCode}/batches/{warehouseCode}` | Admin, Cashier, StockController, Manager | Batches available to allocate against a line (`strategy`, default `FEFO`) |
| POST | `/api/Invoice/{docEntry}/fiscalize` | Admin, Cashier | Fiscalise a posted invoice |

**Query parameters for `/paged`:** `page` (1), `pageSize` (20), `docNum`, `cardCode`, `fromDate`,
`toDate`, `vanSalesOnly`

There is **no update and no delete route**: an invoice is created by posting it to SAP, and
correcting one is a credit note.

**Proof of delivery and attachments** — also on this controller:

| Method | Endpoint | Roles | Description |
|--------|----------|-------|-------------|
| POST | `/api/Invoice/{docEntry}/pod` | Admin, Cashier, PodOperator, Operator, Driver, SalesRep | Upload a POD against an invoice |
| POST | `/api/Invoice/{docEntry}/crate-pod` | Admin, Manager, Merchandiser, PodOperator, Operator, Driver | Upload a crate POD |
| POST | `/api/Invoice/pods/validate-bulk` | Admin, Cashier, PodOperator, Operator, Driver, SalesRep | Check a batch of invoices for existing PODs before uploading |
| GET | `/api/Invoice/pods` | Admin, Cashier, PodOperator, Operator, Driver, SalesRep | List PODs |
| GET | `/api/Invoice/pod-upload-status` | Admin, Cashier, PodOperator, Driver, SalesRep, ApiUser | Upload-status report |
| GET | `/api/Invoice/pod-dashboard` | Admin, Cashier, PodOperator, Driver, SalesRep | POD dashboard figures |
| GET | `/api/Invoice/{docEntry}/attachments` | Admin, Cashier, PodOperator, Operator, Driver, SalesRep | An invoice's attachments |
| GET | `/api/Invoice/{docEntry}/attachments/{attachmentId}/download` | Admin, Cashier, PodOperator, Operator, Driver, SalesRep | Download one |

**Create Invoice Request:**

```json
{
  "cardCode": "C0001",
  "docDate": "2026-04-01",
  "docDueDate": "2026-04-30",
  "numAtCard": "PO-12345",
  "comments": "Standard order",
  "docCurrency": "USD",
  "salesPersonCode": 1,
  "u_Van_saleorder": "VSO-001",
  "lines": [
    {
      "itemCode": "PRD001",
      "quantity": 10,
      "unitPrice": 25.99,
      "warehouseCode": "WH01",
      "taxCode": "X1",
      "discountPercent": 0,
      "uoMCode": "Each",
      "uoMEntry": 1
    }
  ]
}
```

**Additional query parameters for creation:**
- `autoAllocateBatches` (bool, default: `true`) — auto-allocate batch numbers for batch-managed items
- `allocationStrategy` (`FEFO` | `FIFO`) — batch allocation strategy
- `warehouseCode` — required for batch-managed items

**Invoice Response:**

```json
{
  "docEntry": 12345,
  "docNum": 1001,
  "docDate": "2026-04-01",
  "docDueDate": "2026-04-30",
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "numAtCard": "PO-12345",
  "comments": "Standard order",
  "docStatus": "Open",
  "docTotal": 259.90,
  "paidToDate": 0,
  "vatSum": 40.28,
  "docCurrency": "USD",
  "customerVatNo": "VAT123456",
  "customerTinNumber": "TIN789",
  "lines": [
    {
      "lineNum": 0,
      "itemCode": "PRD001",
      "itemDescription": "Widget A",
      "quantity": 10,
      "unitPrice": 25.99,
      "lineTotal": 259.90,
      "warehouseCode": "WH01",
      "discountPercent": 0
    }
  ]
}
```

> **Note:** Invoice creation performs stock validation, batch allocation (FEFO/FIFO), stock locking, SAP posting, and optional fiscalisation in a single transaction.

---

### 11. Credit Notes

**Base route:** `/api/CreditNote`  
**Auth:** Bearer + permissions as noted

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/CreditNote` | `invoices.view` | List credit notes (paginated) |
| GET | `/api/CreditNote/{id}` | `invoices.view` | Get by ID |
| GET | `/api/CreditNote/number/{creditNoteNumber}` | `invoices.view` | Get by credit note number |
| GET | `/api/CreditNote/by-invoice/{invoiceId}` | `invoices.view` | Credit notes for an invoice |
| POST | `/api/CreditNote` | `invoices.create` | Create credit note |
| POST | `/api/CreditNote/from-invoice/{invoiceId}` | `invoices.create` | Create one from an invoice |
| PATCH | `/api/CreditNote/{id}/status` | `invoices.edit` | Change its status |
| POST | `/api/CreditNote/{id}/approve` | `invoices.edit` | Approve it |
| POST | `/api/CreditNote/bulk-cancel` | `invoices.edit` | Cancel many at once |
| POST | `/api/CreditNote/duplicate-cancelled` | `invoices.create` | Re-raise cancelled credit notes |
| DELETE | `/api/CreditNote/{id}` | `invoices.delete` | Delete one |

**Query parameters:** `page` (1), `pageSize` (20), `status`, `cardCode`, `fromDate`, `toDate`,
`includeLines` (default **false**)

The list answers **headers only** unless `includeLines=true` is passed, so anything that aggregates
by item — and not just the ones that read `lines` directly — totals zero against the default.

**Credit Note Types:** `Return`, `Adjustment`, `Damage`  
**Credit Note Statuses:** `Draft`, `Pending`, `Approved`, `Cancelled`, `Applied`

**Create Credit Note Request:**

```json
{
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "type": "Return",
  "originalInvoiceId": 1,
  "originalInvoiceDocEntry": 12345,
  "reason": "Damaged goods returned",
  "comments": "",
  "currency": "USD",
  "restockItems": true,
  "restockWarehouseCode": "WH01",
  "lines": [
    {
      "itemCode": "PRD001",
      "itemDescription": "Widget A",
      "quantity": 2,
      "unitPrice": 25.99,
      "discountPercent": 0,
      "taxPercent": 15.5,
      "warehouseCode": "WH01",
      "returnReason": "Damaged in transit",
      "batchNumber": "B2026-001"
    }
  ]
}
```

---

### 12. Sales Orders

**Base route:** `/api/SalesOrder`  
**Auth:** Bearer + permissions as noted

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/SalesOrder` | `salesorders.view` | List sales orders |
| GET | `/api/SalesOrder/{id}` | `salesorders.view` | Get by ID |
| GET | `/api/SalesOrder/local/{id}` | `salesorders.view` | Get the **local** order by its local id |
| GET | `/api/SalesOrder/number/{orderNumber}` | `salesorders.view` | Get by order number |
| GET | `/api/SalesOrder/{id}/pdf` | `salesorders.view` | Download as a PDF |
| GET | `/api/SalesOrder/local/{id}/pdf` | `salesorders.view` | Download the local order as a PDF |
| POST | `/api/SalesOrder` | `salesorders.create` | Create sales order |
| PUT | `/api/SalesOrder/{id}` | `salesorders.edit` | Update sales order |
| PATCH | `/api/SalesOrder/{id}/status` | `salesorders.edit` | Change its status |
| POST | `/api/SalesOrder/{id}/approve` | `salesorders.approve` | Approve it |
| POST | `/api/SalesOrder/{id}/post-to-sap` | `salesorders.post_to_sap` | Post an approved order to SAP |
| POST | `/api/SalesOrder/{id}/convert-to-invoice` | `invoices.create` | Convert to an invoice |
| DELETE | `/api/SalesOrder/{id}` | `salesorders.delete` | Cancel it |
| POST | `/api/SalesOrder/backfill-web-order-tax` | Admin role | One-off tax repair (`dryRun` **true**, `maxPostedOrders` 200) |

**Query parameters:** `page` (1), `pageSize` (20), `status`, `cardCode`, `fromDate`, `toDate`,
`source`, `search`, `vanSalesUsersOnly`

Approving, posting and deleting are three separate permissions, not one: `salesorders.approve`
decides, `salesorders.post_to_sap` commits, and neither implies the other. The backfill defaults to
`dryRun=true` — it reports what it would change unless you say otherwise.

**Sales Order Statuses:** `Draft`, `Pending`, `Approved`, `PartiallyInvoiced`, `Invoiced`, `Cancelled`

**Create Sales Order Request:**

```json
{
  "deliveryDate": "2026-04-15",
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "customerRefNo": "REF-001",
  "comments": "",
  "salesPersonCode": 1,
  "salesPersonName": "John Sales",
  "currency": "USD",
  "discountPercent": 5,
  "shipToAddress": "123 Delivery St",
  "billToAddress": "456 Billing Ave",
  "warehouseCode": "WH01",
  "lines": [
    {
      "itemCode": "PRD001",
      "itemDescription": "Widget A",
      "quantity": 50,
      "unitPrice": 25.99,
      "discountPercent": 0,
      "taxPercent": 15.5,
      "warehouseCode": "WH01",
      "uoMCode": "Each"
    }
  ]
}
```

**Credit limit check**

An order is refused when it would take the customer past the credit limit set on the SAP business
partner. The check runs twice: when the order is created, and again immediately before it is posted
to SAP — a mobile order is priced after capture, so the post is the first point its real value is
known.

Exposure is `OCRD.Balance + this order`, measured against `OCRD.CreditLine` — only what the customer
actually owes counts, so orders already raised but not yet invoiced do not refuse a new one. Where
the account names a consolidating parent (`OCRD.FatherCard`), the parent's limit is measured against
the whole group's combined exposure; an account's own limit still applies alongside it. Accounts
with no limit set are not restricted.

Open orders are left out because SAP will not raise an invoice for a customer that is over its limit
— an order allowed here cannot turn into debt past the limit later, so the two checks cover the
document flow between them. Setting `CreditLimit:IncludeOpenOrders` to `true` adds open orders back
into exposure (and into the refusal message), refusing the order at capture instead of at invoicing.
It is off by default.

Refusals come back as `400` with code `SalesOrder.CreditLimitExceeded` and a message naming the
account, the limit, the balance and the amount over — safe to show to the user as-is:

```json
{
  "status": 400,
  "code": "SalesOrder.CreditLimitExceeded",
  "errors": {
    "SalesOrder.CreditLimitExceeded": [
      "This order would take PinTail Trading (SAI034) over its credit limit. Credit limit USD 30,000.00, current balance USD 35,759.10, this order USD 1,200.00 — USD 6,959.10 over. Take a payment against the account or reduce the order before submitting it again."
    ]
  }
}
```

If SAP cannot be reached the order is allowed through and a warning is logged, so a SAP outage does
not stop order capture.

**Evening credit review**

A Quartz job (`credit-limit-review`) sweeps every customer once at 19:15 CAT — after the day's
invoicing and payments are in — and raises a notification naming the accounts and groups already
sitting over their limit, whose orders will be refused at capture the next day. The worst ten are
named in the notification; the full list goes to the log at Warning. It is silent when nothing is
over. The notification reaches Admin, Cashier and SalesRep users.

**Credit control endpoints**

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/credit-control/over-limit` | `customers.view` | Accounts and groups currently over their credit limit |
| GET | `/api/credit-control/headroom` | `customers.view` | How much credit room named customers have left |

Same finding as the evening review, on demand and in full. Served from a 10-minute cache; pass
`?refresh=true` to re-read SAP, which is what to do after taking a payment. Concurrent callers
share one sweep rather than each triggering their own.

`headroom` answers from that same sweep, so asking about the customers on a page of pending orders
costs no extra SAP reads. Pass `?cardCodes=SPA077&cardCodes=FOO030` or a comma-separated list, up to
100 per call. Each account reports the limit that actually governs it — for a consolidated account
that is the parent's, under `creditAccountCardCode`, because that is the limit the order will be
refused on and the account a payment has to be made against. `hasLimit: false` means no limit is set
on the account or its parent, which is not the same as no room left; `headroom` is negative when the
account is already over.

It exists because a refusal arrives too late to act on: on 2026-08-20 the same order was pushed at
SPA077 four times, each attempt spending 8 to 26 seconds re-pricing against live SAP before the
credit gate refused it, and the fourth — already cut from 1,050.48 to 794.82 — was still 8.75 over.
The account had 786.07 of room throughout.

```json
{
  "generatedAt": "2026-07-29T09:15:04Z",
  "fromCache": false,
  "customersRead": 1840,
  "limitsMeasured": 612,
  "breachCount": 2,
  "totalOver": 9959.10,
  "breaches": [
    {
      "cardCode": "SAI034",
      "cardName": "PinTail Trading (Pvt) Ltd T/A Sai Mart",
      "currency": "USD",
      "isGroup": false,
      "accountCount": 1,
      "creditLimit": 30000.00,
      "balance": 35759.10,
      "openOrders": 0.00,
      "exposure": 35759.10,
      "amountOver": 5759.10
    }
  ]
}
```

`isGroup: true` means the row is a consolidated group: `cardCode` is the parent and the figures
cover all `accountCount` accounts under it. A breached group is reported once, not once per member.
`generatedAt` is when the SAP sweep ran, not when the request was served — a cached answer can be
several minutes old, which is worth knowing before chasing an account.

Configurable under `CreditLimit` in `appsettings.json`: `Enabled`, `IncludeOpenOrders`,
`EveningReviewEnabled`, `ReviewTimeCAT`, `ReviewNotificationAccountLimit`, `ReviewCacheMinutes`.

---

### 13. Quotations

**Base route:** `/api/Quotation`  
**Auth:** Bearer + permissions as noted

The `quotations.*` family is separate from `invoices.*`, which these endpoints used to borrow. A
quotation is an offer that binds nobody, so raising one is not the trust that raising an invoice is:
Admin, Manager, Cashier and SalesRep hold view/create/edit by default, and only Admin holds delete.
A sales rep therefore quotes a customer and converts the quote to a sales order without ever gaining
the right to invoice.

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/Quotation` | `quotations.view` | List local quotations |
| GET | `/api/Quotation/sap` | `quotations.view` | List SAP quotations |
| GET | `/api/Quotation/sap/{docEntry}` | `quotations.view` | One SAP quotation |
| GET | `/api/Quotation/sap/{docEntry}/pdf` | `quotations.view` | A SAP quotation as a PDF |
| GET | `/api/Quotation/{id}` | `quotations.view` | Get by ID |
| GET | `/api/Quotation/number/{quotationNumber}` | `quotations.view` | Get by quotation number |
| GET | `/api/Quotation/{id}/pdf` | `quotations.view` | Download as a PDF |
| POST | `/api/Quotation` | `quotations.create` | Create quotation |
| PUT | `/api/Quotation/{id}` | `quotations.edit` | Update quotation |
| PATCH | `/api/Quotation/{id}/status` | `quotations.edit` | Change its status |
| POST | `/api/Quotation/{id}/approve` | `quotations.edit` | Approve it |
| POST | `/api/Quotation/{id}/apply-standard-vat` | `quotations.edit` | Re-apply the standard VAT rate to every line |
| PUT | `/api/Quotation/{id}/reprice` | `quotations.edit` | Reprice it against current prices |
| POST | `/api/Quotation/{id}/convert-to-sales-order` | `quotations.create` | Convert to a sales order |
| DELETE | `/api/Quotation/{id}` | `quotations.delete` | Delete it |

**Query parameters:** `page` (1), `pageSize` (20), `cardCode`, `fromDate`, `toDate`; the local list
also takes `status`

The `sap/*` routes read SAP directly and are keyed by `docEntry`; everything else is the local
quotation, keyed by its own id. A `{docEntry}` and an `{id}` are not interchangeable.

**Quotation Statuses:** `Draft`, `Pending`, `Approved`, `Converted`, `Expired`, `Cancelled`

**Create Quotation Request:**

```json
{
  "validUntil": "2026-05-01",
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "customerRefNo": "RFQ-001",
  "contactPerson": "Jane Buyer",
  "comments": "",
  "termsAndConditions": "Payment within 30 days",
  "salesPersonCode": 1,
  "currency": "USD",
  "discountPercent": 0,
  "warehouseCode": "WH01",
  "lines": [
    {
      "itemCode": "PRD001",
      "itemDescription": "Widget A",
      "quantity": 100,
      "unitPrice": 24.99,
      "discountPercent": 5,
      "taxPercent": 15.5,
      "warehouseCode": "WH01"
    }
  ]
}
```

---

### 14. Purchase Orders

**Base route:** `/api/PurchaseOrder`  
**Auth:** Bearer + purchasing permissions

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/PurchaseOrder` | `purchasing.view` | List local purchase orders |
| GET | `/api/PurchaseOrder/sap` | `purchasing.view` | List SAP purchase orders |
| GET | `/api/PurchaseOrder/sap/{docEntry}` | `purchasing.view` | One SAP purchase order |
| GET | `/api/PurchaseOrder/{id}` | `purchasing.view` | Get by ID |
| GET | `/api/PurchaseOrder/number/{orderNumber}` | `purchasing.view` | Get by order number |
| POST | `/api/PurchaseOrder` | `purchasing.create` | Create purchase order |
| PUT | `/api/PurchaseOrder/{id}` | `purchasing.edit` | Update purchase order |
| PATCH | `/api/PurchaseOrder/{id}/status` | `purchasing.edit` | Change its status |
| POST | `/api/PurchaseOrder/{id}/approve` | `purchasing.approve` | Approve it |
| POST | `/api/PurchaseOrder/{id}/receive` | `purchasing.receive` | Receive goods |
| DELETE | `/api/PurchaseOrder/{id}` | `purchasing.delete` | Delete it |
| POST | `/api/PurchaseOrder/documents/upload` | `purchasing.upload_documents` | Attach a document (multipart; `poReferenceNumber`, `description`) |
| GET | `/api/PurchaseOrder/documents` | `purchasing.view` | Attached documents (`poReferenceNumber`) |

**Query parameters:** `page` (1), `pageSize` (20), `cardCode`, `fromDate`, `toDate`; the local list
also takes `status`

Documents are keyed by the **PO reference number**, not by the order's id, so one can be uploaded
before the order exists here. The other four purchasing documents are in
[Purchasing Documents](#48-purchasing-documents).

**Purchase Order Statuses:** `Draft`, `Pending`, `Approved`, `PartiallyReceived`, `Received`, `Cancelled`, `OnHold`

**Receive Goods Request:**

```json
{
  "comments": "Received at dock 3",
  "warehouseCode": "WH01",
  "lines": [
    {
      "lineNum": 0,
      "itemCode": "PRD001",
      "quantityReceived": 45,
      "warehouseCode": "WH01",
      "batchNumber": "B2026-005"
    }
  ]
}
```

---

### 15. Incoming Payments

**Base route:** `/api/IncomingPayment`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/IncomingPayment` | Create incoming payment (posts to SAP) |
| GET | `/api/IncomingPayment` | List incoming payments |
| GET | `/api/IncomingPayment/{docEntry}` | Get by SAP DocEntry |
| GET | `/api/IncomingPayment/docnum/{docNum}` | Get by document number |
| GET | `/api/IncomingPayment/customer/{cardCode}` | Customer's payments (paginated) |
| GET | `/api/IncomingPayment/daterange` | Payments between two dates (`fromDate`, `toDate`, both required) |
| GET | `/api/IncomingPayment/today` | Today's payments |
| GET | `/api/IncomingPayment/queue/{externalReference}` | Queue status for a deferred payment |
| POST | `/api/IncomingPayment/{docEntry}/attachment` | Upload an attachment against a payment |

There is no route that lists the payments against a given invoice — reach them through the
customer's payments and match on the invoice yourself.

**Incoming Payment DTO:**

```json
{
  "docEntry": 5001,
  "docNum": 2001,
  "docDate": "2026-04-01",
  "docDueDate": "2026-04-01",
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "docCurrency": "USD",
  "cashSum": 0,
  "checkSum": 0,
  "transferSum": 259.90,
  "creditSum": 0,
  "docTotal": 259.90,
  "remarks": "Bank transfer payment",
  "transferReference": "TRF-2026-001",
  "transferDate": "2026-04-01",
  "transferAccount": "_SYS00000000089",
  "paymentInvoices": [
    {
      "lineNum": 0,
      "docEntry": 12345,
      "sumApplied": 259.90,
      "invoiceType": "it_Invoice"
    }
  ]
}
```

---

### 16. Payment Gateways

**Base route:** `/api/Payment`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/Payment/providers` | **anonymous** | Get available payment providers |
| POST | `/api/Payment/initiate` | `ApiAccess` | Initiate a payment transaction |
| GET | `/api/Payment/{id}/status` | `ApiAccess` | Check payment status |
| GET | `/api/Payment/transactions` | `ApiAccess` | Transaction history (`provider`, `status`, `page` 1, `pageSize` 50) |
| POST | `/api/Payment/{id}/cancel` | `ApiAccess` | Cancel a payment |
| POST | `/api/Payment/{id}/refund` | **AdminOnly** | Refund one (`amount`; a partial refund when given) |
| POST | `/api/Payment/callback/paynow` | **anonymous** | PayNow's callback |
| POST | `/api/Payment/callback/innbucks` | **anonymous** | Innbucks' callback |
| POST | `/api/Payment/callback/ecocash` | **anonymous** | Ecocash's callback |

The three callbacks are anonymous because the gateway is not a user; they are the routes to point a
provider's webhook configuration at. `/refund` is the one route on this controller behind
`AdminOnly`.

**Supported Providers:** `PayNow`, `Innbucks`, `Ecocash`

**Initiate Payment Request:**

```json
{
  "provider": "Ecocash",
  "amount": 259.90,
  "currency": "USD",
  "phoneNumber": "+263771234567",
  "email": "customer@example.com",
  "invoiceId": "INV-1001",
  "customerCode": "C0001",
  "reference": "Payment for INV-1001",
  "returnUrl": "https://app.example.com/payment/complete",
  "callbackUrl": "https://api.example.com/api/Payment/callback"
}
```

**Initiate Payment Response:**

```json
{
  "transactionId": "txn-uuid-here",
  "externalTransactionId": "ECO-12345",
  "status": "Pending",
  "provider": "Ecocash",
  "paymentUrl": null,
  "qrCode": null,
  "ussdCode": "*151*2*1*amount#",
  "instructions": "Approve the payment on your phone",
  "expiresAt": "2026-04-01T12:30:00Z"
}
```

**Payment Statuses:** `Pending`, `Processing`, `Success`, `Failed`, `Cancelled`, `Refunded`, `Expired`

---

### 17. Inventory Transfers

**Base route:** `/api/InventoryTransfer`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/InventoryTransfer` | Submit an inventory transfer for approval (`stock.transfer` or `inventory.transfer`) |
| GET | `/api/InventoryTransfer/detail/{docEntry}` | Get one transfer's details |
| GET | `/api/InventoryTransfer/{warehouseCode}` | Transfers for a warehouse — the bare `{}` segment is a **warehouse code, not a DocEntry** |
| GET | `/api/InventoryTransfer/{warehouseCode}/paged` | The same, paginated |
| GET | `/api/InventoryTransfer/{warehouseCode}/date/{date}` | A warehouse's transfers on one date |
| GET | `/api/InventoryTransfer/{warehouseCode}/daterange` | A warehouse's transfers between two dates |
| GET | `/api/InventoryTransfer/pending` | Transfers held for approval (`status`, `warehouseCode`, `mineOnly`, `page`, `pageSize`, `fromDate`, `toDate`) |
| GET | `/api/InventoryTransfer/pending/{id}` | One held transfer, posting status included |
| POST | `/api/InventoryTransfer/pending/{id}/decision` | Approve or reject a held transfer |
| POST | `/api/InventoryTransfer/pending/{id}/post` | Retry the SAP post for an approved transfer |
| POST | `/api/InventoryTransfer/pending/{id}/cancel` | Cancel a held transfer |
| POST | `/api/InventoryTransfer/request` | Raise a transfer request — ask a warehouse for stock |
| GET | `/api/InventoryTransfer/requests` | List transfer requests, newest first (`page`, `pageSize`, `status`) |
| PATCH | `/api/InventoryTransfer/request/{docEntry}` | Change an open request's lines and warehouses. Admin, StockController, DepotController, Manager |
| POST | `/api/InventoryTransfer/request/{docEntry}/convert` | Authorize a request and generate the SAP transfer. Admin, StockController, DepotController |
| POST | `/api/InventoryTransfer/request/{docEntry}/close` | Close a request in SAP without converting it. Admin, StockController, DepotController |
| GET | `/api/InventoryTransfer/requests/{warehouseCode}` | A warehouse's transfer requests |
| GET | `/api/InventoryTransfer/request/{docEntry}` | One transfer request |
| GET | `/api/InventoryTransfer/request-edits` | List changes held for approval (`status`, `requestDocEntry`, `page`, `pageSize`) |
| GET | `/api/InventoryTransfer/request-edits/{id}` | One held change |
| POST | `/api/InventoryTransfer/request-edits/{id}/decision` | Approve or reject a held change |
| POST | `/api/InventoryTransfer/request-edits/{id}/cancel` | Withdraw a change the caller proposed |

##### POST `/api/InventoryTransfer/request`

Raise a request for stock. This is the asking end of the flow the rest of the `/request/*` routes
serve: it creates the SAP transfer request, and nothing moves until somebody with the authority
converts it.

**Body:** `CreateTransferRequestDto`.

```json
{
  "toWarehouse": "CORMACH2",
  "fromWarehouse": "KEFSHOP",
  "docDate": "2026-09-04",
  "dueDate": "2026-09-08",
  "comments": "Weekend cover",
  "lines": [
    { "itemCode": "CHE011", "quantity": 24, "uoMCode": "Each" }
  ]
}
```

`toWarehouse` and at least one line are required, and every line needs `itemCode` and `quantity`.
Everything else is optional: `docDate` defaults to today, and `fromWarehouse` is a default that each
line may override with its own `fromWarehouseCode` / `toWarehouseCode` — one request can therefore
draw from several warehouses, which is what makes a single request for a week's shortfall possible
rather than one request per source.

The requester is taken from the caller's token, not from the body. `requesterEmail`,
`requesterName`, `requesterBranch` and `requesterDepartment` are carried onto the SAP document as
descriptive fields; they do not decide who the request belongs to and cannot be used to raise one as
somebody else.

**Response:** `201 Created`, `Location` naming `GET /api/InventoryTransfer/request/{docEntry}`, body
`{ "message": "...", "transferRequest": { … } }`.

**Listing transfer requests:** `status` filters on the SAP document status — `open`, `closed`, or
`all` (the default; the SAP literals `bost_Open` and `bost_Close` are accepted too). Any other value
returns 400. SAP holds around eleven thousand requests, most of them closed, so pass `status=open`
when listing requests to be actioned, and page rather than walking the whole set — a page of 100
takes roughly 5–10 seconds because every row is enriched with its approval state.

Enrichment reports approval state; it never opens it. `approvalStatus` and `approvalStages` are
populated only for requests raised through this API, which open an approval request as they are
created. A request raised directly in SAP has none, so it comes back with `approvalStatus: null` and
an empty `approvalStages`, and `documentStatus` is the only status it carries.

`POST /api/InventoryTransfer/request/{docEntry}/convert` applies the same rule regardless of where
the request originated. An Admin or StockController converts it outright and generates the SAP
transfer in one call. A DepotController may submit conversion only when the source warehouse is in
their `assignedWarehouseCodes`; otherwise the call returns 403. A depot-controller submission does
not post directly to SAP: it creates or continues the request's approval process, and the transfer
is generated only after that process completes. For a request raised directly in SAP this opens
Stock Officer Approval. For an app-raised request, the existing approval history is continued.
Every successful submission and conversion is recorded in the audit log.

`POST /api/InventoryTransfer/request/{docEntry}/close` turns a request down, and closes the SAP
document either way. For a request raised directly in SAP that is the whole of it — there is no
approval to decide, so nothing is routed and no stage can refuse the caller. For a request raised
through this API the rejection is recorded against its approval process first, and the document is
closed once that rejection is final; a stage still waiting on further refusals leaves it open. Both
paths enforce the same source-warehouse scope as `convert`. If SAP will not close the document the
call answers 400 `InventoryTransfer.TransferRequestCloseFailed` rather than reporting a request
closed that is still open — repeating the call records the same decision again and retries the
close.

**Create Transfer Request:**

```json
{
  "fromWarehouse": "WH01",
  "toWarehouse": "WH02",
  "docDate": "2026-04-01",
  "dueDate": "2026-04-05",
  "comments": "Restocking branch warehouse",
  "lines": [
    {
      "itemCode": "PRD001",
      "quantity": 20,
      "fromWarehouseCode": "WH01",
      "toWarehouseCode": "WH02"
    }
  ]
}
```

#### Approval gate on direct transfers

`POST /api/InventoryTransfer` validates quantities, warehouse codes and stock, then holds the
transfer locally and opens the configured approval process. A DepotController's transfer always
routes to Stock Officer approval, including transfers between warehouses assigned to that depot
controller. No interactive inventory transfer posts directly to SAP from this endpoint.

A held transfer returns `202 Accepted`:

```json
{
  "message": "Inventory transfer submitted for approval. It will post to SAP once all approval stages are complete.",
  "requiresApproval": true,
  "transfer": null,
  "statusUrl": "https://…/api/InventoryTransfer/pending/3f2a…",
  "pendingTransfer": {
    "id": "3f2a…",
    "status": "AwaitingApproval",
    "fromWarehouse": "WH01",
    "toWarehouse": "WH02",
    "approvalStatus": "Pending",
    "approvalStages": [{ "stageName": "Stock Officer Approval", "status": "Pending" }]
  }
}
```

The transfer posts to SAP on the **final** approval, and only then does `transfer` carry a
`docEntry` / `docNum`. Stock is re-validated immediately before posting, because it can move while
the transfer waits. Send `Idempotency-Key` (or `clientRequestId`) to make resubmission safe — a
repeat returns the existing held transfer rather than opening a second approval.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/InventoryTransfer/pending` | List held transfers. `status` (default `AwaitingApproval`, or `all`), `warehouseCode`, `mineOnly`, `page`, `pageSize` |
| GET | `/api/InventoryTransfer/pending/{id}` | Held transfer with lines and per-stage approval progress |
| POST | `/api/InventoryTransfer/pending/{id}/decision` | Approve or reject. Admin, StockController, DepotController, Manager |
| POST | `/api/InventoryTransfer/pending/{id}/post` | Retry the SAP post after a `PostFailed` |
| POST | `/api/InventoryTransfer/pending/{id}/cancel` | Withdraw. Submitter or Admin only, before any decision |

**Decision body:**

```json
{ "decision": "Approved", "stageId": null, "remarks": "Confirmed against stock count" }
```

`decision` is `Approved` or `NotApproved`. Omit `stageId` to let the API pick the caller's pending
stage. `POST /api/approval-process/transfers/{id}/decision` is an equivalent route.

**Statuses:** `AwaitingApproval` → `Approved` → `Posted`, or `Rejected` / `Cancelled`.
`PostFailed` means the approval stands but SAP rejected the post; `lastError` explains why and the
`/post` endpoint retries.

#### Changing a transfer request

`PATCH /api/InventoryTransfer/request/{docEntry}` rewrites an open request. `lines` is the complete
set of lines the request should be left with — anything omitted is removed — and either warehouse
may be reassigned by naming it. Omit a warehouse, or send it blank, to leave the request's own:

```json
{
  "lines": [{ "lineNum": 0, "quantity": 6 }],
  "fromWarehouse": "WH02",
  "toWarehouse": null,
  "reason": "WH01 cannot cover this"
}
```

A warehouse change is written to the header and to every kept line, since it is the line's own
warehouse that moves the stock. A closed request returns `409`
`InventoryTransfer.TransferRequestNotEditable`; a change that would leave the request moving stock
from a warehouse to itself returns `400`.

Callers assigned the request's source warehouse write straight to SAP (`200`). Anyone else has the
change held for approval (`202`, `requiresApproval: true`) and it reaches SAP only on the final
approval — the held record carries `proposedFromWarehouse` / `proposedToWarehouse` so the approver
sees the move. One held change per request; a second returns `409`
`InventoryTransfer.TransferRequestEditInFlight`.

#### Warehouse scoping

**Depot controllers** may only action transfers whose **source** warehouse is one of their
`assignedWarehouseCodes` — converting or rejecting a transfer request, and deciding on or posting a
held direct transfer. Violations return `403` with
`InventoryTransfer.WarehouseNotAssigned`, or `InventoryTransfer.NoAssignedWarehouses` when the
account has no warehouses at all. Administrators are unrestricted, and other roles are not
warehouse-scoped.

When a DepotController creates an actual transfer, it is always routed to approval rather than
posted directly. It remains unposted until a StockController approves it, regardless of whether the
source and destination warehouses are assigned to the DepotController.

A depot controller may also only **name** their own warehouses: `fromWarehouse` and `toWarehouse` on
a request change must each be one of their `assignedWarehouseCodes`, or the call is refused with
`403` `InventoryTransfer.WarehouseNotAssigned`. Unlike the rules above this is never routed to
approval — a warehouse they do not run is not something an approver can bless. The proposer's scope
is re-checked when an approved change is applied, so a reassignment stops working the moment the
warehouse leaves their account (the held change goes to `ApplyFailed` with `lastError` explaining).

---

### 18. Business Partners

**Base route:** `/api/BusinessPartner`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/BusinessPartner` | Get all business partners from SAP |
| GET | `/api/BusinessPartner/type/{cardType}` | Filter by type |
| GET | `/api/BusinessPartner/search?q={q}` | Search by code or name |
| GET | `/api/BusinessPartner/batch?cardCodes={a,b,c}` | Read up to 200 named partners in one call |
| GET | `/api/BusinessPartner/groups` | The BP group codes `groupCode` points at |
| GET | `/api/BusinessPartner/paymentterms/{groupNumber}` | One payment terms group |
| GET | `/api/BusinessPartner/{cardCode}` | Get specific business partner |

**Card Types:** `cCustomer`, `cSupplier`, `cLead`

##### GET `/api/BusinessPartner/batch`

| Parameter | Notes |
|-----------|-------|
| `cardCodes` | Repeat the parameter, or comma-separate, or both |

`?cardCodes=SPA059&cardCodes=NRI049` and `?cardCodes=SPA059,NRI049` are the same request. Codes are
trimmed, blanks dropped, and duplicates removed case-insensitively before the read.

**At most 200 codes**, or `400 BusinessPartner.TooManyCodes` — the cap is on the de-duplicated list,
so it counts what will actually be fetched. This exists so a page rendering a list of documents can
resolve every partner name it needs in one call instead of one call per row; a SAP read costs the
same whether it names one partner or a hundred, and the per-row shape is what makes a list page slow.

**Response:** `BusinessPartnerListResponseDto`, the same shape the list endpoint returns. Codes that
name no partner are simply absent — a batch read is not an assertion that every code exists, and
failing the whole call over one dead code would make the caller fall back to reading one at a time.

##### GET `/api/BusinessPartner/groups`

**Response:**

```json
{
  "count": 12,
  "groups": [
    { "code": 100, "name": "Retail" },
    { "code": 101, "name": "Wholesale" }
  ]
}
```

`code` is SAP's `BusinessPartnerGroups.Code`, which is what the `groupCode` on a partner points at —
the two are the same number, so this is how a group code is turned into a name.

##### GET `/api/BusinessPartner/paymentterms/{groupNumber}`

`groupNumber` is route-constrained to an integer, so a non-numeric segment does not reach the action
at all — that is a routing 404 with no body, not the domain 404 the string-keyed routes on this
controller return.

**Response:**

```json
{
  "groupNumber": 3,
  "paymentTermsGroupName": "30 Days",
  "numberOfAdditionalDays": 30,
  "numberOfAdditionalMonths": 0
}
```

`404 BusinessPartner.PaymentTermsNotFound` for a group SAP does not hold. Read live from SAP like
the rest of this controller, so it answers `BusinessPartner.SapDisabled` when the integration is
switched off.

**Business Partner DTO:**

```json
{
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "cardType": "cCustomer",
  "groupCode": 100,
  "phone1": "+263771234567",
  "phone2": null,
  "email": "accounts@abc.co.zw",
  "address": "123 Main St",
  "city": "Harare",
  "country": "ZW",
  "currency": "USD",
  "balance": 1500.00,
  "isActive": true,
  "priceListNum": 1,
  "priceListName": "Base Price",
  "vatRegNo": "VAT123456",
  "tinNumber": "TIN789012"
}
```

---

### 19. Exchange Rates

**Base route:** `/api/ExchangeRate`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/ExchangeRate` | Get all active exchange rates |
| GET | `/api/ExchangeRate/{fromCurrency}/{toCurrency}` | Current rate between two currencies |
| GET | `/api/ExchangeRate/{fromCurrency}/{toCurrency}/history?days={days}` | Rate history (default 30 days) |
| GET | `/api/ExchangeRate/convert?from={from}&to={to}&amount={amount}` | Convert an amount |

**Exchange Rate DTO:**

```json
{
  "id": 1,
  "fromCurrency": "USD",
  "toCurrency": "ZIG",
  "rate": 25.75,
  "inverseRate": 0.0388,
  "effectiveDate": "2026-04-01",
  "source": "RBZ",
  "isActive": true,
  "createdAt": "2026-04-01T08:00:00Z"
}
```

---

### 20. GL Accounts

**Base route:** `/api/GLAccount`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/GLAccount` | Get all G/L accounts from SAP |
| GET | `/api/GLAccount/type/{accountType}` | Filter by type |
| GET | `/api/GLAccount/{accountCode}` | Get specific account |
| GET | `/api/GLAccount/{accountCode}/ledger` | Journal postings for one account |

**Account Types:** `at_Revenues`, `at_Expenses`, `at_Other`

**Ledger query:** `?fromDate=yyyy-MM-dd&toDate=yyyy-MM-dd`, defaulting to the current month to date.
Returns the period's journal lines with a running balance, plus `sapBalance` and
`computedBalanceToday` — the account's balance as SAP reports it and as the journal sums to, so the
two can be compared. `reconciliationDifference` is their difference and is expected to be zero;
`isReconciled` is false when the check could not be run at all, which is not the same as agreeing.
Capped at 5,000 lines; `isTruncated` says the tail was dropped. There is no total line count — the
capped read never sees one — so a truncated period is only ever "more than the limit".

---

### 21. Cost Centres

**Base route:** `/api/CostCentre`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/CostCentre` | Get all active cost centres from SAP |
| GET | `/api/CostCentre/dimension/{dimension}` | Filter by dimension (1-5) |
| GET | `/api/CostCentre/{centerCode}` | Get specific cost centre |

**Cost Centre DTO:**

```json
{
  "centerCode": "CC001",
  "centerName": "Head Office",
  "dimension": 1,
  "isActive": true,
  "validFrom": "2025-01-01",
  "validTo": null,
  "displayName": "CC001 - Head Office"
}
```

---

### 22. Documents

**Base route:** `/api/Document`  
**Auth:** Bearer + ApiAccess (Admin/Manager for create/update)

#### Templates

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Document/templates` | List templates (`documentType`, `activeOnly` default **true**, `page` 1, `pageSize` 20) |
| GET | `/api/Document/templates/{id}` | Get template by ID |
| GET | `/api/Document/templates/default/{documentType}` | Get default template for a document type |
| GET | `/api/Document/templates/placeholders/{documentType}` | The placeholders a template of that type may use |
| POST | `/api/Document/templates` | Create template (Admin/Manager) |
| PUT | `/api/Document/templates/{id}` | Update template |
| DELETE | `/api/Document/templates/{id}` | Delete template |
| POST | `/api/Document/templates/{id}/set-default` | Make it the default for its document type |

The list filter is `documentType`, not `type`.

#### Generating and sending

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/Document/generate` | Render a document |
| POST | `/api/Document/generate/download` | Render it and return the file |
| POST | `/api/Document/email` | Render it and email it |
| GET | `/api/Document/history` | What was generated (`documentType`, `entityId`, `page` 1, `pageSize` 20) |

#### Signatures

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/Document/signatures` | Sign a document |
| GET | `/api/Document/signatures` | Signatures on one (`documentType` and `documentId`, both required) |
| POST | `/api/Document/signatures/{id}/verify` | Verify one |

#### Email templates

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Document/email-templates` | List (`activeOnly`, default **true**) |
| GET | `/api/Document/email-templates/{templateCode}` | One, **by its code** |
| POST | `/api/Document/email-templates` | Create one |
| PUT | `/api/Document/email-templates/{id}` | Update one, **by its id** |

`GET` takes a template *code* and `PUT` takes an *id* — the same path segment, two different keys.

**Document Types:** `Invoice`, `CreditNote`, `SalesOrder`, `Quotation`, `PurchaseOrder`, `Statement`, `DeliveryNote`

**Template DTO:**

```json
{
  "id": 1,
  "name": "Standard Invoice",
  "documentType": "Invoice",
  "htmlContent": "<html>...</html>",
  "cssStyles": "body { font-family: sans-serif; }",
  "headerContent": "<div>Company Logo</div>",
  "footerContent": "<div>Terms & Conditions</div>",
  "paperSize": "A4",
  "orientation": "Portrait",
  "isDefault": true,
  "isActive": true
}
```

#### Attachments

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Document/attachments` | List attachments for an entity — `entityType` and `entityId` are **query parameters**, not path segments |
| GET | `/api/Document/attachments/{attachmentId}/download` | Download one attachment |
| POST | `/api/Document/attachments` | Upload attachment (multipart form) |
| DELETE | `/api/Document/attachments/{attachmentId}` | Delete attachment |

---

### 23. Reports

**Base route:** `/api/Report`  
**Auth:** Bearer + `reports.view` permission  
**Cache:** All report endpoints are cached for 15 minutes (900 seconds)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Report/sales-summary` | Sales summary for a date range |
| GET | `/api/Report/top-products` | Top selling products |
| GET | `/api/Report/top-customers` | Top customers |
| GET | `/api/Report/slow-moving-products` | Products with no movement (`daysThreshold`, default 30) |
| GET | `/api/Report/stock-summary` | Stock on hand **and its value**, optionally for one warehouse |
| GET | `/api/Report/stock-movement` | Stock movement over a date range |
| GET | `/api/Report/low-stock-alerts` | Low stock alert items (`warehouseCode`, `threshold`) |
| GET | `/api/Report/receivables-aging` | Customer aging analysis |
| GET | `/api/Report/payment-summary` | Payments over a date range |
| GET | `/api/Report/account-sales-payments` | Sales and payments per account |
| GET | `/api/Report/order-fulfillment` | Order fulfilment |
| GET | `/api/Report/credit-notes` | Credit notes over a date range |
| GET | `/api/Report/purchase-orders` | Purchase orders over a date range |
| GET | `/api/Report/merchandiser-purchase-orders` | Merchandiser-raised purchase orders |
| GET | `/api/Report/profit-overview` | Profit overview |
| GET | `/api/Report/item-volume-sales` | Net quantity, converted volume and net revenue per item and business partner |

**Common query parameters:** `fromDate`, `toDate`, `warehouseCode`, `top` (for top N)

Inventory valuation lives on `stock-summary` — `totalStockValueUsd` / `totalStockValueZig` overall
and `totalValueUsd` / `totalValueZig` per warehouse. There has never been an `inventory-value` route.

**Item Volume & Revenue**

Backs both the item volume report and the customer revenue report — they read the
same invoice and credit-note lines, so one call serves both.

| Parameter | Notes |
|-----------|-------|
| `fromDate`, `toDate` | Default to the last 30 days. Both invoices and credit notes are selected on their own `DocDate`. |
| `grouping` | `Daily`, `Weekly`, `Monthly`, `Quarterly` or `Total`. Default `Monthly`. `Total` returns the whole window as one period, starting on `fromDate` rather than at the head of a calendar period. |
| `accountCodes` | Repeatable. Required. A `PREFIX001-019` token is expanded into its members. |
| `itemCodes` | Repeatable. Empty means every item the selected accounts traded. |

Quantities and amounts are **net**: a credit note dated in the window is deducted
from it. Volume is net quantity multiplied by the item's active factor from
`/api/ItemVolumeConversion`; an item with no active factor contributes **no**
volume and is listed in `itemCodesWithoutFactor`, so `summary.netVolume` is a
floor whenever `summary.itemsWithoutFactorCount` is non-zero.

```json
{
  "generatedAtUtc": "2026-08-05T06:14:22Z",
  "fromDateUtc": "2026-07-01T00:00:00Z",
  "toDateUtc": "2026-07-31T00:00:00Z",
  "grouping": "Monthly",
  "requestedAccountCodes": ["CIS006", "MAC006"],
  "requestedItemCodes": [],
  "itemCodesWithoutFactor": ["NEW001"],
  "summary": {
    "requestedAccountCount": 2,
    "activeAccountCount": 2,
    "itemCount": 34,
    "invoiceCount": 412,
    "creditNoteCount": 37,
    "invoicedQuantity": 29300.00,
    "creditedQuantity": 1156.00,
    "netQuantity": 28144.00,
    "netVolume": 12480.500,
    "itemsWithoutFactorCount": 1,
    "quantityWithoutFactor": 1204.00,
    "netRevenueUsd": 32180.00,
    "netRevenueZig": 41200.00
  },
  "itemTotals": [
    {
      "itemCode": "YOG143",
      "itemName": "Greek Yoghurt 500ml",
      "invoicedQuantity": 4120.00,
      "creditedQuantity": 86.00,
      "netQuantity": 4034.00,
      "volumeFactor": 0.6,
      "hasVolumeFactor": true,
      "netVolume": 2420.400,
      "netRevenueUsd": 10085.00,
      "netRevenueZig": 0
    }
  ],
  "accountTotals": [],
  "periods": [],
  "documentLines": []
}
```

---

### 23a. Item Volume Conversions

**Base route:** `/api/ItemVolumeConversion`  
**Auth:** Bearer + API access

The volume one sold unit of an item represents, used by
`/api/Report/item-volume-sales`. Seeded from the business' catalogue on start,
insert-only, so a factor edited here is never overwritten.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/ItemVolumeConversion` | List factors. `search` matches code and name; `includeInactive` defaults to true |
| PUT | `/api/ItemVolumeConversion/{itemCode}` | Create or replace a factor |
| DELETE | `/api/ItemVolumeConversion/{itemCode}` | Remove a factor |

Item codes are stored upper-cased, so `PUT /api/ItemVolumeConversion/yog143`
updates the same row as `YOG143`.

```json
{
  "itemName": "Greek Yoghurt 500ml",
  "volumeFactor": 0.6,
  "notes": "500ml tub",
  "isActive": true,
  "updatedBy": "ngoni"
}
```

Clearing `isActive` retires the factor: the item is still reported, but as
unconverted rather than at a stale factor.

**Sales Summary Response:**

```json
{
  "totalInvoices": 245,
  "totalSalesUSD": 125000.50,
  "totalSalesZIG": 3218762.88,
  "totalVatUSD": 19375.08,
  "totalVatZIG": 498908.25,
  "averageInvoiceValueUSD": 510.20,
  "averageInvoiceValueZIG": 13138.22,
  "uniqueCustomers": 48,
  "dailySales": [
    {
      "date": "2026-04-01",
      "invoiceCount": 12,
      "totalSalesUSD": 5230.50,
      "totalSalesZIG": 134685.38
    }
  ],
  "salesByCurrency": [
    {
      "currency": "USD",
      "invoiceCount": 180,
      "totalSales": 125000.50,
      "totalVat": 19375.08
    }
  ]
}
```

**Low Stock Alert Response:**

```json
{
  "reportDate": "2026-04-01",
  "totalAlerts": 15,
  "criticalCount": 3,
  "warningCount": 12,
  "items": [
    {
      "itemCode": "PRD005",
      "itemName": "Widget E",
      "warehouseCode": "WH01",
      "currentStock": 5,
      "reorderLevel": 50,
      "minimumStock": 10,
      "alertLevel": "Critical",
      "suggestedReorderQty": 100
    }
  ]
}
```

---

### 24. Statements

**Base route:** `/api/Statement`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Statement/{cardCode}` | The statement as data |
| GET | `/api/Statement/{cardCode}/pdf` | The same statement as a PDF |
| GET | `/api/Statement/generate/{cardCode}` | The same PDF, older path |

All three take the same query parameters:

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | three months ago | Date only; a time component is dropped |
| `toDate` | today | Date only |
| `cardCodes` | — | Extra accounts to consolidate into one statement; repeatable |

`fromDate` after `toDate` is refused rather than answered empty.

##### One statement, two accounts

`cardCodes` exists because one shop is one SAP card code **per currency** — "SPA059" and
"SPA059 USD" are the same customer keeping two ledgers. Passing the others in `cardCodes` builds a
single consolidated statement over all of them; the route's own `{cardCode}` is always included, and
the set is trimmed and de-duplicated case-insensitively, so naming it twice is harmless.

`customer.accountStructure` reports which happened — `"Multi"` when more than one code went into the
statement, `"Single"` otherwise — so a reader can tell a consolidated statement from a plain one
without counting the codes back.

##### The PDF paths are one action

`{cardCode}/pdf` and `generate/{cardCode}` are two routes on the same method and answer identically;
the second is the older spelling, kept because callers still use it. Both return
`application/pdf` as a file download.

##### GET `/api/Statement/{cardCode}`

**Response:** `CustomerStatementResponseDto` — `customer`, `fromDate`, `toDate`, `generatedAt`,
`openingBalance`, `totalDebits`, `totalCredits`, `totalInvoices`, `totalPayments`,
`totalCreditNotes`, `closingBalance`, `lines[]` and an `aging` summary.

Statements are built **behind a cache that outlives the request**. A statement over a long period
can take longer than the caller's HTTP timeout, so the build is not abandoned when the client gives
up — it finishes, and the next identical request is served from it. The cache key is the account
set plus the two dates, so changing either builds a new one. A build that exceeds its own budget
answers `Statement.Timeout`; asking again is what picks up the finished result.

---

### 25. Notifications

**Base route:** `/api/Notification`  
**Auth:** Bearer + ApiAccess

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Notification` | Get notifications (paginated, filterable by type/read status) |
| GET | `/api/Notification/unread-count` | Get unread notification count |
| POST | `/api/Notification/mark-read` | Mark notifications as read |
| POST | `/api/Notification` | Create notification (Admin only) |
| DELETE | `/api/Notification/{id}` | Delete a notification |

**Notification Types:** `Info`, `Warning`, `Error`, `Success`, `Alert`  
**Notification Categories:** `LowStock`, `Payment`, `Invoice`, `System`

**Create Notification Request:**

```json
{
  "title": "Low Stock Alert",
  "message": "Widget A is below reorder level in WH01",
  "type": "Warning",
  "category": "LowStock",
  "entityType": "Product",
  "entityId": "PRD001",
  "actionUrl": "/products/PRD001",
  "targetUsername": null,
  "targetRole": "StockController"
}
```

**Mark Read Request:**

```json
{
  "notificationIds": [1, 2, 3]
}
```

Pass `null` for `notificationIds` to mark all as read.

---

### 26. Webhooks

**Base route:** `/api/Webhook`  
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Webhook` | List all webhooks |
| GET | `/api/Webhook/{id}` | Get webhook details |
| POST | `/api/Webhook` | Create webhook subscription |
| PUT | `/api/Webhook/{id}` | Update webhook |
| DELETE | `/api/Webhook/{id}` | Delete webhook |
| POST | `/api/Webhook/{id}/test` | Send test event to webhook |
| GET | `/api/Webhook/deliveries` | Delivery attempts (`webhookId`, `page` 1, `pageSize` 50) |
| GET | `/api/Webhook/event-types` | **Anonymous.** The event types available to subscribe to |

`GET /api/Webhook/event-types` is the one route here outside the Admin role — it is a static
vocabulary, and a subscriber needs it before it has anything to authenticate with.

**Supported Event Types:**

| Category | Events |
|----------|--------|
| **Invoice** | `invoice.created`, `invoice.paid`, `invoice.cancelled` |
| **Payment** | `payment.received`, `payment.failed`, `payment.refunded` |
| **Stock** | `stock.low`, `stock.out`, `stock.replenished`, `stock.transfer` |
| **Inventory** | `inventory.adjusted`, `inventory.received` |
| **Customer** | `customer.created`, `customer.updated` |
| **SAP** | `sap.sync.success`, `sap.sync.failed`, `sap.connection.lost`, `sap.connection.restored` |

**Create Webhook Request:**

```json
{
  "name": "Stock Alerts",
  "url": "https://hooks.example.com/stock",
  "secret": "whsec_abc123",
  "events": ["stock.low", "stock.out"],
  "retryCount": 3,
  "timeoutSeconds": 30,
  "customHeaders": {
    "X-Custom-Header": "value"
  }
}
```

**Webhook Delivery Payload:**

```json
{
  "event": "stock.low",
  "timestamp": "2026-04-01T10:30:00Z",
  "data": { ... },
  "signature": "sha256=..."
}
```

The `signature` is an HMAC-SHA256 of the payload body using the webhook secret.

---

### 27. Backups

**Base route:** `/api/Backup`  
**Auth:** Bearer + backup permissions

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/Backup` | `backups.view` | List all backups (paginated) |
| GET | `/api/Backup/{id}` | `backups.view` | Get backup details |
| GET | `/api/Backup/stats` | `backups.view` | Backup statistics |
| GET | `/api/Backup/capabilities` | `backups.view` | What this deployment can actually do — which backup and restore paths are available |
| GET | `/api/Backup/{id}/download` | `backups.view` | Download the backup file |
| POST | `/api/Backup` | `backups.create` | Create new backup |
| POST | `/api/Backup/{id}/restore` | `backups.restore` | Restore from one |
| DELETE | `/api/Backup/{id}` | `backups.delete` | Delete one |
| POST | `/api/Backup/reset-database` | **Admin role** | Reset the database |

> `POST /api/Backup/reset-database` is destructive and gated on the Admin **role** rather than on a
> backup permission. Check `/api/Backup/capabilities` before assuming a restore path exists in the
> environment you are pointed at.

**Create Backup Request:**

```json
{
  "backupType": "Full",
  "description": "Pre-deployment backup",
  "uploadToCloud": false
}
```

**Backup Stats Response:**

```json
{
  "totalBackups": 45,
  "successfulBackups": 43,
  "failedBackups": 2,
  "totalSizeBytes": 1073741824,
  "totalSizeFormatted": "1.00 GB",
  "lastBackupAt": "2026-04-01T06:00:00Z",
  "nextScheduledBackup": "2026-04-02T06:00:00Z",
  "backupsLast24Hours": 2,
  "backupsLast7Days": 14
}
```

---

### 28. Rate Limit Management

**Base route:** `/api/RateLimit`  
**Auth:** Bearer + permissions as noted

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/RateLimit` | `users.edit` | List all rate limits (`page` 1, `pageSize` 20, `blockedOnly`) |
| GET | `/api/RateLimit/client/{clientId}` | `users.edit` | Get client's rate limit info |
| GET | `/api/RateLimit/current` | ApiAccess | Get current request's rate limit status |
| GET | `/api/RateLimit/check` | ApiAccess | Check if request would be allowed (non-incrementing) |
| POST | `/api/RateLimit/block/{clientId}` | `users.edit` | Block a client |
| POST | `/api/RateLimit/unblock/{clientId}` | `users.edit` | Unblock one |
| POST | `/api/RateLimit/reset/{clientId}` | `users.edit` | Clear a client's counter **without** lifting a block |
| GET | `/api/RateLimit/blocked` | `users.edit` | Every client currently blocked |
| GET | `/api/RateLimit/stats` | `users.edit` | Totals across all clients |
| GET | `/api/RateLimit/config` | `users.edit` | The limits in force |
| PUT | `/api/RateLimit/config` | `users.edit` | Change them, without a restart — see below |
| POST | `/api/RateLimit/cleanup` | `users.edit` | Clear expired counters |

Rate limit administration is gated on `users.edit`, not on a rate-limit permission of its own —
there isn't one.

##### `reset/{clientId}` is not `unblock/{clientId}`

`reset` zeroes `requestCount` and restarts the window, and that is all: `isBlocked` and
`blockExpiresAt` are left where they were, so **resetting a blocked client leaves it blocked**.
`unblock` clears the block *and* zeroes the counter. Reset is for a client whose window filled
unfairly — a retry storm, a batch job — where the block has not landed yet; unblock is for one that
is already shut out. Both `404` on a client id that has no rate limit row, which is the answer for a
client that has never been counted.

##### `config` changes the limiter that actually returns 429

`GET` returns the limits in force. `PUT` changes them: the values are stored in `SystemConfigs` and
picked up by the ASP.NET Core rate limiter — the one that rejects requests — **without a restart**.

This is what the limits map onto:

| Field | Effect |
|-------|--------|
| `maxRequests` | Requests per client per window before `429`, on the `fixed` and `api` policies |
| `windowSizeSeconds` | Length of that window |
| `isEnabled` | `false` stops partitioning unauthenticated callers **per IP** — see the warning below |
| `whitelistedIPs` | Addresses exempt from rate limiting entirely |
| `whitelistedApiKeys` | `X-API-Key` values exempt from rate limiting. Exempts from *throttling* only, and grants no access: a key still has to be a real one under `Security:ApiKeys` to authenticate |
| `blockDurationMinutes` | How long `/api/RateLimit` blocks a client for. Does **not** affect the ASP.NET Core limiter, which does not block |

The stricter limit protecting the `auth` endpoints, and the queue depth, are deliberately **not**
settable here — they are deployment settings, and a write that never mentioned them leaves them
alone rather than resetting them.

**`isEnabled: false` widens the limit, it does not remove it.** With IP partitioning off, every
unauthenticated caller shares a single `anonymous` bucket — one limit for the whole internet, which
the first bot exhausts for every real customer. It is a diagnostic setting, not an off switch.

**A change gives every client a fresh window.** The limiter builds a client's partition once and
caches it, so the settings are folded into the partition key: changed settings mean a new partition
built with the new limits. That is what makes a change reach a client already being throttled — the
one it is usually being made for — at the cost of resetting everyone's current window. Limits move
rarely; a change that silently failed to apply would be worse.

**Propagation is not instant.** Each instance re-reads at most every 10 seconds, so allow that long
for a change to take everywhere. The instance that served the `PUT` applies it immediately.

**Refusals.** `maxRequests` outside 1–1,000,000, `windowSizeSeconds` outside 1–86,400,
`blockDurationMinutes` outside 0–43,200, or a `whitelistedIPs` entry that is not an IP address, are
refused with `400 RateLimit.InvalidConfiguration` and nothing is written. The bounds are not taste:
a permit limit of `0` makes the limiter throw while building a partition — on the request path, for
every request — so saving one would take the API down and no restart would clear it.

With nothing ever set, the configured values apply: `RateLimit:PermitLimit`,
`RateLimit:WindowSeconds` (defaults 100 and 60) and `blockDurationMinutes` 15.

**Blocked clients** — `GET /api/RateLimit/blocked` returns `List<ApiRateLimitDto>`, the same shape
the list endpoint returns, filtered to those currently blocked.

**Stats:**

```json
{
  "totalClients": 412,
  "activeClients": 38,
  "blockedClients": 2,
  "totalRequestsToday": 91744,
  "totalBlocksToday": 6,
  "averageRequestsPerClient": 222.7
}
```

**Config:**

```json
{
  "maxRequests": 100,
  "windowSizeSeconds": 60,
  "blockDurationMinutes": 15,
  "isEnabled": true,
  "whitelistedIPs": [],
  "whitelistedApiKeys": []
}
```

**Rate Limit Status Response:**

```json
{
  "clientId": "user:admin",
  "requestsInWindow": 45,
  "maxRequests": 100,
  "windowSizeSeconds": 60,
  "windowResetAt": "2026-04-01T10:31:00Z",
  "isBlocked": false,
  "blockedUntil": null,
  "remainingRequests": 55
}
```

---

### 29. SAP Settings

**Base route:** `/api/sap-settings`  
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sap-settings` | Get current SAP settings (password masked) |
| PUT | `/api/sap-settings` | Update SAP connection settings |
| POST | `/api/sap-settings/test-connection` | Test SAP connectivity |

**Update SAP Settings Request:**

```json
{
  "serviceLayerUrl": "https://sap-server:50000/b1s/v1",
  "companyDB": "SBO_Production",
  "userName": "manager",
  "password": "new-password",
  "testConnection": true
}
```

**Connection Test Response:**

```json
{
  "success": true,
  "message": "Connected successfully",
  "responseTimeMs": 245,
  "testedAt": "2026-04-01T10:00:00Z"
}
```

---

### 30. Desktop Integration

**Base route:** `/api/DesktopIntegration`  
**Auth:** Bearer + ApiAccess

This controller supports stock reservations and queue-based invoice posting for the desktop application.

#### Stock Reservations

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/DesktopIntegration/reservations` | Create stock reservation (holds inventory) |
| GET | `/api/DesktopIntegration/reservations` | List reservations (`sourceSystem`, `status`, `cardCode`, `externalReferenceId`, `activeOnly` default **true**, `page` 1, `pageSize` 20) |
| GET | `/api/DesktopIntegration/reservations/{reservationId}` | Get reservation details |
| GET | `/api/DesktopIntegration/reservations/by-reference/{externalReferenceId}` | Find a reservation by the caller's own reference |
| POST | `/api/DesktopIntegration/reservations/confirm` | Confirm and post to SAP |
| POST | `/api/DesktopIntegration/reservations/cancel` | Cancel reservation |
| POST | `/api/DesktopIntegration/reservations/renew` | Extend a reservation before it expires |

Confirm, cancel and renew are **POSTs that take the reservation id in the body**, not verbs on
`/reservations/{id}` — there is no `PUT` or `DELETE` anywhere on this controller's reservations.

**Create Reservation Request:**

```json
{
  "externalReferenceId": "DESKTOP-INV-001",
  "externalReference": "Desktop Invoice",
  "sourceSystem": "DesktopApp",
  "documentType": "Invoice",
  "cardCode": "C0001",
  "cardName": "ABC Trading",
  "currency": "USD",
  "reservationDurationMinutes": 60,
  "requiresFiscalization": true,
  "priority": 1,
  "notes": "",
  "lines": [
    {
      "lineNum": 0,
      "itemCode": "PRD001",
      "itemDescription": "Widget A",
      "quantity": 10,
      "uoMCode": "Each",
      "warehouseCode": "WH01",
      "unitPrice": 25.99,
      "taxCode": "X1",
      "autoAllocateBatches": true
    }
  ]
}
```

> **Note:** Reservations hold physical stock for the specified duration (default 60 minutes). Stock is released if the reservation is not confirmed before expiry.

#### Invoice Queue

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/DesktopIntegration/invoices` | Reserve and confirm in one call, on the request |
| POST | `/api/DesktopIntegration/invoices/queued` | Queue an invoice for async posting (answers `202`) |
| GET | `/api/DesktopIntegration/queue/{externalReference}` | Check queue status by the caller's own reference |
| GET | `/api/DesktopIntegration/queue/by-reservation/{reservationId}` | Check queue status by reservation |
| GET | `/api/DesktopIntegration/queue` | The queue (`sourceSystem`, `limit` 100) |
| GET | `/api/DesktopIntegration/queue/review` | Queue entries needing a human look (`limit` 50) |
| GET | `/api/DesktopIntegration/queue/stats` | Queue counts |
| POST | `/api/DesktopIntegration/queue/{externalReference}/retry` | Retry a failed queue entry |
| DELETE | `/api/DesktopIntegration/queue/{externalReference}` | Drop a queue entry |

Queue entries are addressed by the caller's own `externalReference` throughout — there is no
server-side queue id in any of these routes.

The queue routes sit under `/queue`, **not** under `/invoices/queue*` — the invoice routes create,
the queue routes track.

#### Batch Validation

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/DesktopIntegration/invoices/validate` | Validate an invoice and its batch allocations. `autoAllocateBatches` defaults to `true` and `allocationStrategy` to `FEFO` |
| POST | `/api/DesktopIntegration/stock/validate` | Validate stock availability for a set of lines |

#### Reading documents back

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/DesktopIntegration/invoices/{docEntry}` | One invoice |
| GET | `/api/DesktopIntegration/invoices/by-docnum/{docNum}` | One invoice by DocNum |
| GET | `/api/DesktopIntegration/invoices/customer/{cardCode}` | A customer's invoices (`fromDate`, `toDate`) |
| GET | `/api/DesktopIntegration/invoices/date-range` | Invoices between two dates (`fromDate`, `toDate`, both required) |
| GET | `/api/DesktopIntegration/invoices/paged` | Invoices, paginated (`page` 1, `pageSize` 20) |
| GET | `/api/DesktopIntegration/invoices/{docEntry}/pdf` | An invoice as a PDF (`fiscalQrCode`) |
| GET | `/api/DesktopIntegration/credit-notes/by-docnum/{docNum}` | One credit note by DocNum |
| POST | `/api/DesktopIntegration/sales-orders/convert-to-invoice` | Convert a sales order |
| POST | `/api/DesktopIntegration/fiscal-transactions` | Sync a fiscal transaction back to the local projection |

The PDF route takes the QR payload as a **query parameter** rather than composing it — the desktop
already holds the fiscalised receipt and passes what it was given.

#### Stock

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/DesktopIntegration/stock/{warehouseCode}` | A warehouse's stock (`itemCodes`, comma-separated) |
| GET | `/api/DesktopIntegration/stock/{warehouseCode}/{itemCode}` | One item's stock |
| GET | `/api/DesktopIntegration/stock/{warehouseCode}/{itemCode}/batches` | Its batches |
| GET | `/api/DesktopIntegration/stock/{warehouseCode}/local` | The local snapshot (`snapshotDate`) |
| GET | `/api/DesktopIntegration/stock/monitored-warehouses` | Which warehouses are snapshotted |
| POST | `/api/DesktopIntegration/stock/fetch-daily` | Take today's snapshot now |

#### Transfers

The transfer surface mirrors the invoice one: post directly, or queue and track. Note the queue for
transfers is `transfer-queue`, separate from the invoice `queue`.

| Method | Endpoint | Roles | Description |
|--------|----------|-------|-------------|
| POST | `/api/DesktopIntegration/transfers` | Admin, ApiUser | Post a transfer on the request |
| POST | `/api/DesktopIntegration/transfers/queued` | Admin, ApiUser | Queue one |
| POST | `/api/DesktopIntegration/transfers/validate` | (class) | Validate before posting |
| GET | `/api/DesktopIntegration/transfers/{docEntry}` | (class) | One transfer |
| GET | `/api/DesktopIntegration/transfers/warehouse/{warehouseCode}` | (class) | A warehouse's transfers |
| GET | `/api/DesktopIntegration/transfers/warehouse/{warehouseCode}/paged` | (class) | The same, paginated |
| GET | `/api/DesktopIntegration/transfers/warehouse/{warehouseCode}/date-range` | (class) | The same, between two dates |
| POST | `/api/DesktopIntegration/transfer-requests` | (class) | Raise a transfer request |
| GET | `/api/DesktopIntegration/transfer-requests/{docEntry}` | (class) | One request |
| GET | `/api/DesktopIntegration/transfer-requests/warehouse/{warehouseCode}` | (class) | A warehouse's requests |
| GET | `/api/DesktopIntegration/transfer-requests/paged` | (class) | Requests, paginated |
| POST | `/api/DesktopIntegration/transfer-requests/{docEntry}/convert` | Admin, StockController, DepotController | Authorise and generate the transfer |
| POST | `/api/DesktopIntegration/transfer-requests/{docEntry}/close` | Admin, StockController, DepotController | Close without converting |
| GET | `/api/DesktopIntegration/transfer-queue` | (class) | The transfer queue (`sourceSystem`, `limit` 100) |
| GET | `/api/DesktopIntegration/transfer-queue/review` | (class) | Entries needing a look (`limit` 50) |
| GET | `/api/DesktopIntegration/transfer-queue/stats` | (class) | Queue counts |
| GET | `/api/DesktopIntegration/transfer-queue/{externalReference}` | (class) | One entry |
| POST | `/api/DesktopIntegration/transfer-queue/{externalReference}/retry` | (class) | Retry it |
| DELETE | `/api/DesktopIntegration/transfer-queue/{externalReference}` | (class) | Drop it |
| POST | `/api/DesktopIntegration/webhook/transfer-event` | (class) | Take a transfer event from SAP |

#### Desktop sales and end of day

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/DesktopIntegration/sales` | Record a desktop sale |
| GET | `/api/DesktopIntegration/sales` | The sales (`warehouseCode`, `cardCode`, `consolidationStatus`, `fromDate`, `toDate`, `page` 1, `pageSize` 50) |
| POST | `/api/DesktopIntegration/end-of-day/consolidate` | Consolidate the day's sales |
| GET | `/api/DesktopIntegration/end-of-day/report` | The day's report (`reportDate`) |
| POST | `/api/DesktopIntegration/end-of-day/email-report` | Email it (`reportDate`) |

#### Prices

A second price surface for the desktop, separate from [Prices](#9-prices).

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/DesktopIntegration/prices/pricelists` | The price lists (`forceRefresh`) |
| GET | `/api/DesktopIntegration/prices/pricelists/{priceListNum}` | One list (`forceRefresh`) |
| GET | `/api/DesktopIntegration/prices/pricelists/{priceListNum}/items/{itemCode}` | One item's price on one list |
| GET | `/api/DesktopIntegration/prices/business-partner/{cardCode}` | A customer's prices |
| POST | `/api/DesktopIntegration/prices/sync` | Sync prices |
| POST | `/api/DesktopIntegration/prices/pricelists/sync` | Sync the lists |
| POST | `/api/DesktopIntegration/prices/pricelists/{priceListNum}/sync` | Sync one list |

Note the spelling: this controller uses `prices/business-partner/{cardCode}` with a hyphen, where
[Prices](#9-prices) uses `businesspartner/{cardCode}` without one.

---

### 31. Customer Portal

**Base route:** `/api/CustomerPortal`  
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/CustomerPortal/generate-hash` | Generate a BCrypt password hash (development only) |

**Portal accounts are not created through this API.** `register` and `bulk-register` were removed: they
hashed a password, discarded it, created nothing and returned success anyway. Portal accounts live in
the Web app's database, which this API cannot reach, so an account written here is one the portal's own
login would never find.

Creating, suspending and resetting portal accounts — individually or in bulk — is done on the Web app's
**Customer Portal Management** page, which owns that table. `generate-hash` remains because it is useful
precisely for the reason the others could not work: it returns a hash to place in that other database
by hand.

---

### 32. Fiscalisation

**There is no fiscal proxy in this API.** REVMax was decommissioned and the `/api/revmax/*` routes were
removed with it. Fiscalisation now runs against the ZIMRA FDMS platform at
<https://fiscal.kefaloscheese.com/>, which this API calls as a client.

Device status, licences, Z-reports, fiscal-day open/close and the receipt archive are functions of that
platform's own console, behind its own login. They are not exposed through ShopInventory.

**How this API uses it**

| Purpose | Platform endpoint |
|---------|-------------------|
| Fiscalise an invoice or credit note already in SAP | `POST /api/sap/receipts/fiscalise` — takes only the SAP `DocEntry`; the platform reads the document from SAP itself |
| Fiscalise a desktop/POS invoice before it reaches SAP | `POST /api/receipts/submit` — full receipt payload |
| Read fiscal status back | `GET /api/receipts/check?deviceId=0&invoiceNo=…&receiptType=…` |
| Device configuration (QR base URL, serial, active taxes) | `GET /api/fiscal-config` — no `deviceId` unless one is pinned |

Authentication is an `X-API-Key` header. The key is configured as `Fiscalisation__ApiKey` and needs the
`receipt.submit`, `sap.fiscalise` and `device.read` scopes, and no device allowlist — a device-scoped key
forces an explicit device id on every call, which breaks failover.

**Which device fiscalises**

`Fiscalisation__DefaultDeviceId` is unset by default and should stay that way: a submission that names no
device makes the platform try every device it has, in order, until one takes the receipt, and it only
moves on where it knows FDMS recorded nothing. The device that actually took it comes back on the
response, so the QR payload and the serial on the document follow the failover.

Zero means different things on the two kinds of call, which is why this API never sends it literally.
On a submission it means "any device". On a read it is a validation error — `GET /api/fiscal-config` and
`GET /api/fiscal-status` fall back to the console's own device only when `deviceId` is *absent*, and
answer `400 ValidationFailed` ("DeviceId is required and must be greater than 0") to an explicit
`deviceId=0`. `GET /api/receipts/check` is the exception that does take `deviceId=0`, meaning "search
every device".

**Managing that key**

**Base route:** `/api/fiscalisation-settings`
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/fiscalisation-settings` | Current fiscalisation settings; the API key comes back masked |
| PUT | `/api/fiscalisation-settings` | Store a new API key |
| POST | `/api/fiscalisation-settings/test-connection` | Check a key against the platform |

The key is written into `web.config`'s `environmentVariables`, the same way SAP connection settings are,
so it survives a deployment without being committed. It is live once the app pool recycles, which
rewriting `web.config` triggers on its own. `GET` always reports the key the process is *running* with,
which is what makes a pending change visible.

**Settings Response:**

```json
{
  "enabled": true,
  "baseUrl": "https://fiscal.kefaloscheese.com/",
  "apiKeyMasked": "••••••••4e6f",
  "isConfigured": true,
  "defaultDeviceId": 0
}
```

**Update Request:**

```json
{
  "apiKey": "fsk_live_…",
  "testConnection": true
}
```

With `testConnection`, the key is read against `GET /api/fiscal-config` before being stored. A key the
platform *refuses* (401/403) is not stored and comes back as a validation error; a platform that cannot
be reached leaves the key stored with `connectionTestPassed: null`, because an outage says nothing about
the key. `POST /test-connection` with a blank or absent `apiKey` tests the key already in force.

**VAT Rate:** 15.5%, configured at `Tax:VatRate`.

**Fiscal fields on an invoice** — `isFiscalized`, `fiscalizationStatus`, `fiscalQrCode`,
`fiscalReceiptGlobalNo`, `fiscalizedAtUtc`. These come from the local projection in
`DesktopFiscalTransactions`, not from a live call.

The QR code is **composed by this API**, not returned by the platform: the verification segment is the
first 16 hex characters of `MD5(deviceSignatureValue)`, appended to the device's `qrUrl` along with the
device id, receipt date and receipt global number. See `FiscalReceiptQrComposer`.

**Triggering fiscalisation**

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/Invoice/{docEntry}/fiscalize` | Fiscalise a posted invoice |

Fiscal status is also reconciled in the background by `InvoiceFiscalStatusBackfillService`; there is
no longer an on-demand backfill endpoint.

---

### 33. Health

**Base route:** `/api/Health`

#### GET `/api/Health`

Health check endpoint. No authentication required.

**Response (200):**

```json
{
  "status": "Healthy",
  "timestamp": "2026-04-01T10:00:00Z"
}
```

#### Probes

These are **not** controller routes — they are `MapHealthChecks` registrations in `Program.cs`, all
anonymous, each selecting its checks by tag. A route sweep of the controllers will not find them.

| Endpoint | Tag | What it answers |
|----------|-----|-----------------|
| `GET /health/live` | `live` | The process is up |
| `GET /health/deploy-ready` | `deploy-ready` | Safe to cut traffic over to this deployment |
| `GET /health/ready` | `ready` | Ready to serve |
| `GET /health/dependencies` | `dependencies` | The downstreams, **SAP included** |

`/health/dependencies` is the only probe whose checks reach SAP, and it runs on the background
queue: it is meant for monitoring to poll, not for a person or a load balancer to block on. Point
liveness and readiness at the first three.

#### Realtime

| Endpoint | Description |
|----------|-------------|
| `/hubs/notifications` | The SignalR hub the web app subscribes to for live notifications |

A hub is not a REST endpoint — connect with a SignalR client, not with `GET`. See
[Notifications](#25-notifications) for the REST side of the same feature.

---

### 34. Van Sales

Van sales has **two API surfaces, and the difference between them is the hyphen**:

| Surface | Base route | Speaks | Callers |
|---------|-----------|--------|---------|
| Portal | `/api/van-sales` | This API's ordinary dialect — camelCase, plain bodies, RFC 7807 problems | The web app's `/van-sales/*` pages |
| Handset | `/api/vansales` | The legacy dialect — snake_case fields, a `{ success, error }` envelope, HTTP 200 on failure | The van sales handset (a separate repo) |

They are separate controllers on purpose. The handset dialect is fixed by builds already in the
field and is not free to change; the portal surface is a plain API and should stay one. Nothing
should be added to `/api/vansales` that a new caller would want.

#### Portal surface

**Base route:** `/api/van-sales`  
**Auth:** Bearer + `ApiAccess` policy, plus the per-endpoint permission below

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/van-sales/compliance-report` | `vansales.attendance.view` | Departure compliance: a row per rep per trading day |
| GET | `/api/van-sales/performance-report` | `vansales.attendance.view` | What sold, by territory and route, by rep, by item, over time |
| GET | `/api/van-sales/coverage-report` | `vansales.attendance.view` | Who the vans are reaching and who they are losing |
| GET | `/api/van-sales/replenishment-report` | `vansales.attendance.view` | How well the depots are keeping the vans stocked |
| GET | `/api/van-sales/stock-report` | `vansales.attendance.view` | What each van carried, sold, and is still riding around with |
| GET | `/api/van-sales/routes` | any of `vansales.attendance.view`, `users.view`, `users.create_merchandiser_accounts` | The selling routes |
| POST | `/api/van-sales/routes` | `users.edit` | Create a route |
| PUT | `/api/van-sales/routes/{id}` | `users.edit` | Update a route |
| GET | `/api/van-sales/visits` | `vansales.attendance.view` | A page of van sales calls, newest first |
| GET | `/api/van-sales/visits/report` | `vansales.attendance.view` | Time on the round, summarised per rep |

`/api/van-sales/routes` takes **any one** of its three permissions, not all three. It has two
unrelated callers — the compliance report's filter and the user editor, where assigning a rep to a
route is part of editing the user — and gating it on van attendance alone empties the editor's route
picker for anyone who administers users without overseeing vans. Silently, because the portal
service swallows the failure and returns an empty list.

**Dates.** Every report here takes **CAT trading days**, not instants: a van's day belongs to the
van, not to the server's zone, and the reports have to count days the same way or a supervisor
reading two of them side by side sees different figures. `visits` is the exception — it filters on
the check-in instant and normalises what it is given to UTC.

The five reports read the same fact stream, so they agree by construction on a period's takings and
its productive calls. Each defaults its own window, and they are not the same: 30 days back for
performance and replenishment, 90 for coverage, 14 for stock.

##### GET `/api/van-sales/compliance-report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 30 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `userId` | — | One rep by id; every rep when omitted |
| `routeCode` | — | One route. Days with **no departure record are excluded** when this is set, because nothing on a loose visit says which route it belonged to |

**Response:** `DepartureComplianceReportResult`. Rates are fractions (0.97), not percentages, and are
**null rather than zero** where the day has no denominator — a CCR of 0% and "we cannot say" are
different findings. Summary rates are recomputed from the period's totals, not averaged across days.

```json
{
  "fromDate": "2026-07-18T00:00:00",
  "toDate": "2026-08-17T00:00:00",
  "days": [
    {
      "vanRouteDayId": 812,
      "userId": "8f14e45f-ceea-467a-9575-1f2b3c4d5e6f",
      "username": "tmoyo",
      "fullName": "T Moyo",
      "tradingDate": "2026-08-15T00:00:00",
      "territory": "Harare North",
      "routeCode": "HN02",
      "routeName": "Harare North 2",
      "truckRegNo": "AEK 4471",
      "timeOut": "2026-08-15T06:40:00",
      "timeIn": "2026-08-15T17:12:00",
      "plannedCustomerCount": 32,
      "customersVisited": 29,
      "productiveCalls": 24,
      "rtiOut": 40,
      "rtiReturned": 38,
      "systemCash": 1840.00,
      "systemEcocash": 320.00,
      "systemInnbucks": 0,
      "systemOther": 150.00,
      "systemUntendered": 60.00,
      "systemTotalSales": 2370.00,
      "declaredCash": 1845.00,
      "declaredEcocash": 320.00,
      "declaredInnbucks": null,
      "currency": "USD",
      "newCustomers": 1,
      "startingMileage": 84210,
      "closingMileage": 84357,
      "hasDayRecord": true,
      "isClosed": true,
      "notes": null,
      "callComplianceRate": 0.90625,
      "productiveCallRate": 0.8275862068965517,
      "averageOrderValue": 98.75,
      "kilometresTravelled": 147,
      "declaredTotal": 2165.00,
      "systemDeclarableTakings": 2160.00,
      "declaredVariance": 5.00,
      "declaredShortfall": null,
      "declaredOverage": null,
      "rtiOutstanding": 2
    }
  ],
  "summary": {
    "dayCount": 22,
    "plannedCustomerCount": 704,
    "customersVisited": 631,
    "productiveCalls": 512,
    "totalSales": 47320.00,
    "newCustomers": 14,
    "kilometresTravelled": 3180,
    "callComplianceRate": 0.8963068181818182,
    "productiveCallRate": 0.8114104595879556,
    "averageOrderValue": 92.42
  }
}
```

**The cash variance is measured against `systemDeclarableTakings`, not `systemTotalSales`.** The
declaration has three boxes — cash, ecocash, innbucks — and the handset offers no fourth, so two
kinds of takings can never appear in `declaredTotal` however honest the rep is:

| Field | What it is | Effect on the variance |
|-------|-----------|------------------------|
| `systemOther` | A named tender with no column — a card swipe, chiefly | None. It settles at the terminal, so the rep never carried it and cannot declare it |
| `systemUntendered` | A sale that named no tender at all, from a handset built before the payment picker | Sets the tolerance. It may well have been cash the rep counted, so it can excuse an over-declaration up to its own value |

`declaredVariance` is `declaredTotal - systemDeclarableTakings`. Read the two findings rather than
the raw variance, because each already allows for the above and both are null when there is nothing
to report:

- `declaredShortfall` — declarable money the rep did not count back. The figure to chase. An
  untendered sale never excuses one: unrecorded money only ever added to what was in their hand.
- `declaredOverage` — money counted back that the day cannot account for even after allowing every
  untendered sale to have been cash they collected. Usually a sale that was made and never recorded.

Until 2026-08-18 the variance subtracted `systemTotalSales`, so any rep whose day included a swipe
or an untendered sale was reported short by exactly the money they had no way to declare.

##### GET `/api/van-sales/performance-report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 30 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `userId` | — | One rep by id; every rep when omitted |
| `routeCode` | — | One route. Sales whose rep opened no departure record are excluded when set, for the reason the compliance report gives |
| `topItems` | `50` | How many items to rank. **Zero or less returns all of them**, not none |

**Response:** `VanSalesPerformanceReportResult` — the period cut by territory and route, by rep, by
item and over time, with the price actually achieved per item and the shape of the drops.

##### GET `/api/van-sales/coverage-report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − **90** days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `userId` | — | One rep by id |
| `routeCode` | — | One route. Sales with no departure record are excluded when set |
| `lapseDays` | `90` | How long a shop may go without buying before it counts as lapsed |
| `granularity` | `Month` | `Week` or `Month` — how the churn and rate series are bucketed |

**Response:** `VanSalesCoverageReportResult` — rate trends, the shops on the books that were not
reached, outlet churn, the win-back register, route concentration, and how the location record is
holding up.

`lapseDays` is deliberately **not** the route-customer pages' dormancy threshold; that one answers a
narrower question about a single shop. The report also reads further back than the period it covers:
the opening state needs a full lapse window behind it, and telling a genuinely new outlet from a
returning one needs an unbounded look at when each shop first bought.

##### GET `/api/van-sales/replenishment-report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 30 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `vanWarehouseCode` | — | One van's warehouse; every van when omitted |

**Response:** `VanReplenishmentReportResult` — how well the depots are keeping the vans stocked, and
which restock requests are stuck.

Built on the pending-transfer table rather than the daily stock snapshot: snapshots are a desktop-app
feature that no van sales path writes to, and the job that fills them is off by default, so a report
built on them would have reported nothing at all rather than failing visibly.

##### GET `/api/van-sales/stock-report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − **14** days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `vanWarehouseCode` | — | One van's warehouse; every van when omitted |
| `deadStockDays` | `14` | Days carried without a sale before a line counts as dead |

**Response:** `VanStockReportResult` — what each van was loaded with, what sold off it, what the next
morning found, which lines are riding the round without selling, and what is about to expire.

The load comes from the morning snapshot and what sold comes from the sales themselves, because no
van sales path maintains the snapshot's running quantity. Reconciliation is morning to morning and is
only computed across **consecutive** snapshots — a missing day is reported as a break rather than
bridged, so a gap reads as a gap instead of as a large one-day variance.

##### GET `/api/van-sales/routes`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `includeInactive` | `false` | Bring back retired routes too; they still head historical days |

**Response:** `List<RouteDto>` — `id`, `code`, `name`, `territory`, `truckRegNo`, `isActive`,
`assignedUserCount`.

##### POST `/api/van-sales/routes` · PUT `/api/van-sales/routes/{id}`

**Body:** `SaveRouteRequest`. There is no delete — a route names historical days.

```json
{
  "code": "HN02",
  "name": "Harare North 2",
  "territory": "Harare North",
  "truckRegNo": "AEK 4471",
  "isActive": true
}
```

**Response:** `RouteDto`. `409 Conflict` on a duplicate code; `PUT` also answers `404` for an
unknown id.

##### GET `/api/van-sales/visits`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `page` | `1` | 1-based |
| `pageSize` | `20` | Not clamped server-side — ask for what you will render |
| `userId` | — | One rep by id |
| `username` | — | One rep by username |
| `customerCode` | — | One shop |
| `fromDate` | — | Inclusive lower bound on check-in time; taken as UTC |
| `toDate` | — | Inclusive upper bound on check-in time; taken as UTC |

**Response:** `VanVisitListResult` — `entries`, `totalCount`, `page`, `pageSize`.

Every row is a van sales call and nothing else: the query is pinned to the van channel rather than
filtered by one, so no caller can widen it to merchandiser rows. `TimesheetController` answers for
merchandisers and is pinned the same way in the other direction; there is no query string that
crosses between them.

```json
{
  "entries": [
    {
      "id": 40122,
      "userId": "8f14e45f-ceea-467a-9575-1f2b3c4d5e6f",
      "username": "tmoyo",
      "fullName": "T Moyo",
      "customerCode": "SHP0431",
      "customerName": "Chitungwiza Tuckshop",
      "checkInTime": "2026-08-15T07:12:00Z",
      "checkOutTime": "2026-08-15T07:34:00Z",
      "checkInLatitude": -17.8252,
      "checkInLongitude": 31.0335,
      "checkOutLatitude": -17.8252,
      "checkOutLongitude": 31.0335,
      "checkInNotes": null,
      "checkOutNotes": null,
      "durationMinutes": 22.0,
      "checkInLocationSource": "Gps",
      "checkOutLocationSource": "Gps",
      "checkInLocationAccuracyMetres": 8.0,
      "checkOutLocationAccuracyMetres": 11.0,
      "locationUnavailableReason": null,
      "checkInRecordedAt": "2026-08-15T07:12:04Z",
      "checkOutRecordedAt": "2026-08-15T09:58:41Z",
      "routeCode": "HN02",
      "routeName": "Harare North 2",
      "truckRegNo": "AEK 4471",
      "wasCapturedOffline": true,
      "syncDelay": "02:24:41"
    }
  ],
  "totalCount": 1184,
  "page": 1,
  "pageSize": 20
}
```

`wasCapturedOffline` and `syncDelay` are computed from the two timestamp pairs, not stored: a call
that reached the server more than two minutes after it happened was queued on the handset.
`routeCode`, `routeName` and `truckRegNo` come from the round's snapshot, so a rep moved to another
route this morning does not rewrite the route on every call they made last month; they are null when
the rep checked into customers without starting a day on the handset.

##### GET `/api/van-sales/visits/report`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 30 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `userId` | — | One rep by id |
| `username` | — | One rep by username |

**Response:** `VanVisitReportResult` — the period's totals plus a `repSummaries` array, each rep
carrying `days` (with the day's individual `calls`, so the page can draw the round as a strip) and
`customers`.

```json
{
  "fromDate": "2026-07-18T00:00:00",
  "toDate": "2026-08-17T00:00:00",
  "repSummaries": [
    {
      "userId": "8f14e45f-ceea-467a-9575-1f2b3c4d5e6f",
      "username": "tmoyo",
      "fullName": "T Moyo",
      "totalCalls": 631,
      "completedCalls": 604,
      "openCalls": 27,
      "offlineCalls": 88,
      "distinctCustomers": 212,
      "tradingDays": 22,
      "totalMinutes": 13288.0,
      "averageMinutesPerCall": 22.0,
      "days": [
        {
          "date": "2026-08-15T00:00:00",
          "callCount": 29,
          "distinctCustomers": 29,
          "openCalls": 1,
          "totalMinutes": 616.0,
          "firstCheckIn": "2026-08-15T07:12:00Z",
          "lastCheckOut": "2026-08-15T15:04:00Z",
          "calls": [
            {
              "customerCode": "SHP0431",
              "customerName": "Chitungwiza Tuckshop",
              "checkInTime": "2026-08-15T07:12:00Z",
              "checkOutTime": "2026-08-15T07:34:00Z"
            }
          ],
          "routeCode": "HN02",
          "routeName": "Harare North 2"
        }
      ],
      "customers": [
        {
          "customerCode": "SHP0431",
          "customerName": "Chitungwiza Tuckshop",
          "callCount": 4,
          "totalMinutes": 81.0
        }
      ],
      "routeCode": "HN02",
      "routeName": "Harare North 2"
    }
  ],
  "totalCalls": 1184,
  "completedCalls": 1131,
  "openCalls": 53,
  "offlineCalls": 174,
  "totalHours": 428.6,
  "averageCallMinutes": 22.7,
  "tradingDays": 22
}
```

Open calls (never checked out) and offline calls (uploaded late) are counted alongside the rest
rather than instead of them — both are routine on a van and both are worth seeing. Averages divide
by `completedCalls`, not `totalCalls`: a call that never closed has no duration to contribute and
dividing by it would drag the average down with time nobody spent.

#### Handset surface (legacy dialect)

**Base route:** `/api/vansales`  
**Auth:** Bearer + `ApiAccess` policy on every route except the two `auth` ones, plus the
per-endpoint permission below  
**Audit:** every call is written to the audit log by `VanSalesAuditFilter`, outcome included

Read this table for the route, the verb and the permission. **Do not treat it as the payload
contract** — the request and response bodies are the handset's dialect, fixed by builds in the
field, and the controller and its `VanSalesLegacy*` DTOs are the only authority on them. Two habits
of that dialect matter before you call anything here:

- Most successes come back wrapped: `{ "success": <payload>, "error": null }`.
- The attendance and trading-day routes answer **HTTP 200 with an error string in the envelope**
  where the rest of this API would answer 4xx. A status code is not enough to tell whether one of
  those calls worked.

| Method | Endpoint | Permission | Notes |
|--------|----------|------------|-------|
| POST | `/api/vansales/auth/login` | anonymous | Rate-limited under the `auth` policy |
| POST | `/api/vansales/auth/refresh` | anonymous | Rate-limited under the `auth` policy |
| POST | `/api/vansales/auth/password` | — (authenticated) | Change own password |
| GET | `/api/vansales/attendance` | `timesheets.view` | The caller's own calls |
| GET | `/api/vansales/attendance/date` | `timesheets.view` | Query parameter is `value` |
| GET | `/api/vansales/attendance/status` | `timesheets.manage` | Whether the caller is checked in |
| POST | `/api/vansales/attendance` | `timesheets.manage` | Check in or out |
| GET | `/api/vansales/day/current` | `timesheets.manage` | The open trading day |
| POST | `/api/vansales/day/start` | `timesheets.manage` | Out of the depot: truck, route, opening odometer |
| POST | `/api/vansales/day/end` | `timesheets.manage` | Back in: closing odometer and the takings counted |
| GET | `/api/vansales/customer` | `customers.view` | The shops on the caller's route |
| POST | `/api/vansales/customer` | `customers.create` | Create a route customer |
| PUT | `/api/vansales/customer/{code}` | `customers.edit` | Correct a shop the caller already services. Keyed by code, because a handset is never given the route customer id. Narrower than the administrator's update: the route, the code and the active flag are read off the row, not taken from the body |
| DELETE | `/api/vansales/customer/{code}` | `customers.delete` | Take a shop off the caller's route. Deactivates rather than removes, so the route keeps its trading history, and resolves the row whether or not it is still active — a removal replayed off the offline queue is ordinary, not an error |
| GET | `/api/vansales/customer/{code}/history` | `customers.view` | What that one shop has bought and still has on order (`from`, `to`). The same detail the office's route customer report reads |
| GET | `/api/vansales/customer/general-trade` | `customers.view` | Every customer the office has classified as General Trade (`OCRD.U_Channel`), company-wide. The only customer read here that is not scoped to the caller's route, so the handler admits `Admin` and `StockController` only. Carries `customers.view` rather than `invoices.view` because a stock controller holds the first and not the second |
| GET | `/api/vansales/customer/{code}/invoices` | `customers.view` | Every invoice SAP holds against one customer, whoever raised it (`from`, `to`, `page`, `pageSize`). Distinct from `{code}/history` above, which answers for a shop on the caller's own route out of this platform's tables; this reads SAP and is not route-scoped. Same two roles |
| POST | `/api/vansales/sales-order` | `salesorders.create` | Create a sales order |
| POST | `/api/vansales/sales-order/history` | `salesorders.view` | Search — a POST because the filter is a body |
| POST | `/api/vansales/order/history` | `invoices.view` | Invoice history; also a POST |
| GET | `/api/vansales/fiscal` | `invoices.view` | Fiscal device details for the handset |
| GET | `/api/vansales/fiscal/lease` | `invoices.create` | Optional `pendingSales`. Returned **bare**, not enveloped |
| POST | `/api/vansales/fiscal/day-close` | `invoices.create` | The close a handset signed for its own fiscal day. Held rather than forwarded — the day is packaged once its receipts have landed |
| POST | `/api/vansales/pod` | `invoices.view` | Upload proof of delivery |
| POST | `/api/vansales/pod/{order}/file` | `invoices.view` | One page of a delivery note, as `multipart/form-data` (`file`, `description`, `externalReference`, `isAdditionalPage`). The van sales mirror of `POST /api/invoice/{docEntry}/pod`, which is gated on a role list carrying `SalesRep` — a different role from the van's `Sales`. Preferred over `POST /api/vansales/pod` above, which carries whole photographs as base64 in a JSON body and sends a note's pages in one request, where the double-submit window reads all but the first as duplicates |
| POST | `/api/vansales/order` | `invoices.create` | Direct invoice. `202` when queued rather than posted |
| POST | `/api/vansales/order/with-batches` | `invoices.create` | The same action as `/order` — one more route on it, not a second endpoint |
| POST | `/api/vansales/sales` | `invoices.create` | Take custody of offline, already-ZIMRA-stamped sales |
| POST | `/api/vansales/order/convert-to-invoice` | `invoices.create` | Always `202` |
| POST | `/api/vansales/stock/position` | `inventory.transfer` | What the van is carrying, as its own handset counts it. Becomes that van's stock snapshot for the trading day — the first count of a day is the one kept |
| POST | `/api/vansales/inventory/request` | `inventory.transfer` | Ask the depot for stock. `201` |
| GET | `/api/vansales/inventory/request` | `inventory.transfer` | The caller's transfer requests |
| POST | `/api/vansales/inventory/confirm` | `inventory.transfer` | Confirm a transfer into the van |

`POST /api/vansales/sales` is not `POST /api/vansales/order` with a flag. Nothing on it reaches SAP
during the request — the batch is held for the end-of-day posting run — and nothing on it is
fiscalised, because the customer is already holding the printed receipt. Per-sale outcomes come back
individually so one bad row cannot strand a van's whole backlog on the handset.

**Related routes on other controllers**

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/Product/van-sale-catalogue` | Bearer + `ApiAccess` | What a van *may be sent* — every van-sale item, not warehouse-scoped and not priced |

That catalogue is deliberately not the same list as
`/api/Product/warehouse/{code}/paged?vanSaleOnly=true`, which answers what a van *can sell* now. A
transfer request needs the first: the items worth asking the depot for are precisely the ones the
van has none of, and those are absent from the warehouse page by design.

---

### 35. Timesheets

**Base route:** `/api/Timesheet`  
**Auth:** Bearer + `ApiAccess`, plus the permission below

Merchandiser attendance. The van counterpart is [Van Sales](#34-van-sales), and the two are
**pinned, not filtered**: this controller answers only for merchandisers, that one only for vans,
neither takes a channel from the caller, and there is no query string that crosses between them.

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| POST | `/api/Timesheet/check-in` | `timesheets.manage` | Check into a customer. Answers `201` |
| POST | `/api/Timesheet/check-out` | `timesheets.manage` | Check out of the open call |
| GET | `/api/Timesheet/active` | `timesheets.manage` | The caller's open call, if any |
| GET | `/api/Timesheet/assigned-customers` | `timesheets.manage` | The customers the caller may check into |
| GET | `/api/Timesheet` | `timesheets.view` | A page of calls |
| GET | `/api/Timesheet/report` | `timesheets.view` | Time on the round, summarised per user |

**`GET /api/Timesheet`:** `page` (1), `pageSize` (20), `userId`, `username`, `customerCode`,
`fromDate`, `toDate`
**`GET /api/Timesheet/report`:** `userId`, `username`, `fromDate`, `toDate`

The report groups on the **raw UTC date**, unlike the van report, which counts CAT trading days —
an 18:30 CAT call is filed under the following day here. The two reports are not interchangeable.

---

### 36. Route Customers

**Base route:** `/api/route-customers`  
**Auth:** Bearer + `ApiAccess`, plus the permission below

The shops on a selling route: the customers a van or merchandiser calls on, as distinct from the SAP
business partner they invoice against. A route customer carries `assignedBusinessPartnerCode`, which
is the link between the two.

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/route-customers` | `customers.view` | The route customers |
| GET | `/api/route-customers/sales-summary` | `customers.view` | Sales per route customer, dormancy included |
| GET | `/api/route-customers/product-mix` | `customers.view` | What they buy |
| GET | `/api/route-customers/{id}/sales` | `customers.view` | One shop's sales (`from`, `to`) |
| POST | `/api/route-customers` | `customers.create` | Create one |
| PUT | `/api/route-customers/{id}` | `customers.edit` | Update one |
| DELETE | `/api/route-customers/{id}` | `customers.delete` | Delete one |
| GET | `/api/route-customers/visit-days` | `customers.view` | Which weekdays the van calls (`routeCustomerId`, `assignedBusinessPartnerCode`) |
| PUT | `/api/route-customers/{id}/visit-days` | `customers.edit` | Replace the calling days for one shop |

| Endpoint | Parameters |
|----------|------------|
| `/api/route-customers` | `assignedBusinessPartnerCode`, `activeOnly` (default **true**) |
| `/sales-summary` | `assignedBusinessPartnerCode`, `from`, `to`, `dormantDays`, `includeInactive` (default **true**) |
| `/product-mix` | `assignedBusinessPartnerCode`, `routeCustomerId`, `from`, `to`, `top` (default `0`, meaning no cap) |

The two list defaults disagree on purpose: the plain list hides inactive shops, the sales summary
counts them, because a shop that went quiet is the thing a dormancy report exists to show.

`sales-summary` separates a **lapsed** shop from one that has **never bought**, and the two counts
are disjoint. Both are read from the all-time `lastSaleAt` rather than from the window's sale count —
a shop with no sales inside the window has not necessarily never bought, and answering it from the
window files every lapsed shop under never-converted. `dormantDays` sets the threshold between them.

The calling days are the **plan** - which weekdays a van is due at a shop - and are what the
ordering app reads to tell a customer their next delivery date and their cut-off. They are not
`VanRouteDayEntity`, which records what a rep actually did on a given day; a van that skips a shop
must not retroactively edit the schedule it is measured against. An empty list is a legitimate state
meaning "not yet known", and a shop in that state can still order: it goes on the next available run.

The handset has its own set, keyed by the customer code rather than by the `{id}` it is never
told: `POST /api/vansales/customer` creates one, `PUT /api/vansales/customer/{code}` corrects
one, `DELETE /api/vansales/customer/{code}` removes one, and
`GET /api/vansales/customer/{code}/history` answers the same question as `{id}/sales` above.
Each resolves which shop on the caller's own route that code names and then hands off to the
handler above, so a van and the office never have two different ways to change or read a route
customer — see [Van Sales](#34-van-sales).

---

### 37. Crates

**Base route:** `/api/crates`  
**Auth:** Bearer + the `ApiAccessWithOperator` policy, plus the roles below

Returnable crates: what went out with a delivery, what came back, and the proof for each.

| Method | Endpoint | Roles | Description |
|--------|----------|-------|-------------|
| GET | `/api/crates/transactions` | Admin, Manager, Merchandiser, PodOperator, Operator, Driver, SalesRep | Crate movements (`search`, `status`, `transactionType`) |
| POST | `/api/crates/transactions/ensure-invoice` | Admin, Manager, Merchandiser, Driver | Open the crate transaction for an invoice if it has none |
| GET | `/api/crates/pods` | Admin, Manager, Merchandiser, PodOperator, Operator, Driver, SalesRep | Crate PODs (`search`, `submissionRole`) |
| POST | `/api/crates/transactions/{id}/pods` | Admin, Manager, Merchandiser, PodOperator, Operator, Driver | Upload a crate POD (multipart) |
| POST | `/api/crates/pods/validate-bulk` | Admin, Manager, Merchandiser, PodOperator, Operator, Driver | Check a batch for PODs already held |
| DELETE | `/api/crates/pods/{id}` | Admin, Manager, Merchandiser, Operator, Driver | Remove a POD |
| GET | `/api/crates/grvs` | Admin, Manager, Merchandiser, Driver, SalesRep | Goods-returned notes (`search`, `status`) |
| POST | `/api/crates/transactions/{id}/grvs` | Admin, Manager, Merchandiser | Raise a GRV against a transaction (multipart) |
| POST | `/api/crates/opening-balances` | Admin | Seed a shop's crate balance (multipart) |
| PUT | `/api/crates/opening-balances/{id}` | Admin | Correct one |
| DELETE | `/api/crates/opening-balances/{id}` | Admin | Remove one |

The multipart routes carry `[MaxRequestBodySize]` of 20 MB — see
[Request size limits](#security-headers--middleware). Both POD and GRV uploads take a
`clientRequestId` form field; see [Idempotency](#idempotency), which they need more than most,
because the handset retries an upload from four different triggers over one queue.

---

### 38. Merchandiser

**Base route:** `/api/Merchandiser`  
**Auth:** Bearer + `ApiAccess`; the exceptions are noted per row

Which products a merchandiser may sell, and the orders they capture on the handset.

**Product assignment.** Every assignment route comes in a pair — one global, one for a named
merchandiser — and the only difference is the `{userId}` segment:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Merchandiser` | The merchandisers |
| GET | `/api/Merchandiser/products` | The globally assigned products |
| GET | `/api/Merchandiser/{userId}/products` | One merchandiser's products |
| POST | `/api/Merchandiser/products` | Assign products globally |
| POST | `/api/Merchandiser/{userId}/products` | Assign products to one merchandiser |
| DELETE | `/api/Merchandiser/products` | Unassign globally |
| DELETE | `/api/Merchandiser/{userId}/products` | Unassign from one merchandiser |
| PUT | `/api/Merchandiser/products/status` | Activate or retire globally |
| PUT | `/api/Merchandiser/{userId}/products/status` | Activate or retire for one merchandiser |
| GET | `/api/Merchandiser/sap-sales-items` | SAP items eligible to be assigned |

Both `DELETE` routes take a **body** (`AssignMerchandiserProductsRequest`), not a query string.

**Handset routes.** These are what the merchandiser app calls:

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/Merchandiser/mobile/categories` | `ApiAccess` | Product categories |
| GET | `/api/Merchandiser/mobile/active-products` | `ApiAccess` | The catalogue (`search`, `category`, `page`, `pageSize`) |
| GET | `/api/Merchandiser/{userId}/active-products` | `ApiAccess` | One merchandiser's catalogue (`search`, `category`) |
| GET | `/api/Merchandiser/mobile/customer/{cardCode}/products` | `ApiAccess` | What may be sold to one customer (`search`, `category`) |
| POST | `/api/Merchandiser/mobile/order` | `salesorders.create` | Capture an order |
| GET | `/api/Merchandiser/mobile/orders` | `ApiAccess` | Captured orders |
| GET | `/api/Merchandiser/mobile/orders/{id}` | `ApiAccess` | One order |
| GET | `/api/Merchandiser/mobile/orders/by-client-request/{clientRequestId}` | `ApiAccess` | Recover an order by its idempotency key — see [Idempotency](#idempotency) |

`pageSize` on `mobile/active-products` defaults to **0**, which means no paging rather than an empty
page. `GET /api/Merchandiser/mobile/orders` takes `page` (1), `pageSize` (20), `status`, `fromDate`,
`toDate`, `search`, `cardCode`.

**Capture does not refuse an order on credit.** A mobile order arrives unpriced, and an unpriced
order can only be measured against the customer's standing balance, so the order is captured, held
on the web, and refused at the point of posting instead. `POST /mobile/order` carries a 5 MB
`[MaxRequestBodySize]`.

| Method | Endpoint | Roles | Description |
|--------|----------|-------|-------------|
| POST | `/api/Merchandiser/backfill-product-details` | Admin | One-off repair of stored product details |
| POST | `/api/Merchandiser/backfill-mobile-order-tax` | Admin | One-off repair of stored order tax |

---

### 39. Sync & SAP Connection

**Base route:** `/api/Sync`  
**Auth:** Bearer + `ApiAccess`; `queue/process` is Admin

The health of this API's link to SAP, and the offline queue that holds documents while it is down.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Sync/status` | The sync dashboard |
| GET | `/api/Sync/sap-connection` | Whether SAP is reachable now |
| GET | `/api/Sync/health` | Health summary |
| GET | `/api/Sync/queue` | Offline queue status |
| GET | `/api/Sync/queue/status` | The same action on a second route |
| GET | `/api/Sync/queue/items` | The queued transactions |
| GET | `/api/Sync/cache-status` | Per-cache sync state |
| GET | `/api/Sync/logs` | Connection log (`count`, default 50) |
| POST | `/api/Sync/test-connection` | Probe SAP now |
| POST | `/api/Sync/queue/{id}/retry` | Retry one queued transaction |
| POST | `/api/Sync/queue/{id}/cancel` | Cancel one |
| POST | `/api/Sync/queue/process` | **Admin.** Drain the queue now |

`/queue` and `/queue/status` are two routes on one action, not two endpoints — they answer
identically, and neither is deprecated.

---

### 40. WhatsApp

**Base route:** `/api/whatsapp`  
**Auth:** Bearer + the `AdminOnly` policy — **except the webhook**, which is anonymous

Outbound and inbound WhatsApp through an OpenWA bridge. A session is one connected handset number;
it is started, scanned from a QR code, and then sends.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/whatsapp/health` | Bridge health |
| GET | `/api/whatsapp/messages` | Inbox (`page` 1, `pageSize` 50, `search`) |
| GET | `/api/whatsapp/sessions` | The sessions |
| POST | `/api/whatsapp/sessions` | Create one. Answers `201` |
| POST | `/api/whatsapp/sessions/{sessionId}/start` | Start it |
| POST | `/api/whatsapp/sessions/{sessionId}/stop` | Stop it |
| GET | `/api/whatsapp/sessions/{sessionId}/qr` | The QR code to scan |
| POST | `/api/whatsapp/sessions/{sessionId}/messages/send-text` | Send a message |
| POST | `/api/whatsapp/sessions/{sessionId}/messages/reply` | Reply to one |
| POST | `/api/whatsapp/webhook/openwa` | **Anonymous.** Inbound from the bridge. `application/json` only, answers `202` |

The webhook is the one route on this controller outside `AdminOnly`, because the bridge is not a
user. Everything else refuses anyone who is not an admin.

---

### 41. Email

**Base route:** `/api/Email`  
**Auth:** Bearer + `ApiAccess`

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/Email/test` | Send a test email to the address in the body, to check the SMTP settings |
| POST | `/api/Email/send` | Send an email now |
| POST | `/api/Email/queue` | Queue one for later (`category`) |
| POST | `/api/Email/process-queue` | Drain the queue now |

Every route here needs a token. `/test` carried `[AllowAnonymous]` until 2026-08-17, which let an
unauthenticated caller make this server send mail from its own SMTP identity to any address they
named; nothing called it that way, and it is now authenticated like the rest.

---

### 42. Push Notifications

**Base route:** `/api/PushNotification`  
**Auth:** Bearer + `ApiAccess`; `/send` is Admin

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| POST | `/api/PushNotification/register` | `ApiAccess` | Register this device's FCM token |
| POST | `/api/PushNotification/unregister` | `ApiAccess` | Drop a token |
| GET | `/api/PushNotification/devices` | `ApiAccess` | The caller's registered devices |
| POST | `/api/PushNotification/send` | **Admin** | Send a push |
| POST | `/api/PushNotification/test` | `ApiAccess` | Send a test push to the caller's own devices |

Not every push is a tray notification: a merchandiser catalogue refresh is a data-only message the
app acts on silently. See [Notifications](#25-notifications) for the in-app bell, which is a
separate mechanism.

---

### 43. Exception Center

**Base route:** `/api/exception-center`  
**Auth:** Bearer + `ApiAccess`

One place for the things that failed and are still waiting on a human — across sources, rather than
one queue per feature. An item is addressed by its `{source}` and `{itemKey}` together, because the
key is only unique within the source.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/exception-center` | The dashboard (`limit`, default 100; `assignee`) |
| POST | `/api/exception-center/items/retry-batch` | Retry many at once |
| POST | `/api/exception-center/items/{source}/{itemKey}/retry` | Retry one |
| POST | `/api/exception-center/items/{source}/{itemKey}/acknowledge` | Mark one as seen |
| POST | `/api/exception-center/items/{source}/{itemKey}/assign-to-me` | Take ownership |

---

### 44. Approval Process

**Base route:** `/api/approval-process`  
**Auth:** Bearer + `ApiAccess`; the stage and template routes are Admin

The approval engine's own configuration, plus the two decision routes that drive it. Stages are the
steps; a template is the sequence of stages a document type goes through.

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/approval-process/stages` | **Admin** | The stages |
| POST | `/api/approval-process/stages` | **Admin** | Create or update one |
| DELETE | `/api/approval-process/stages/{id}` | **Admin** | Delete one |
| GET | `/api/approval-process/templates` | **Admin** | The templates |
| POST | `/api/approval-process/templates` | **Admin** | Create or update one |
| DELETE | `/api/approval-process/templates/{id}` | **Admin** | Delete one |
| POST | `/api/approval-process/transfer-requests/{docEntry}/decision` | `ApiAccess` | Decide a transfer request |
| POST | `/api/approval-process/transfers/{pendingTransferId}/decision` | `ApiAccess` | Decide a held direct transfer |

The two decision routes are not interchangeable: one keys on a SAP `docEntry` (a transfer *request*
that exists in SAP), the other on a local `Guid` (a transfer held here before it ever reaches SAP).
`/api/InventoryTransfer/pending/{id}/decision` decides the same held transfers through the transfer
controller — see [Inventory Transfers](#17-inventory-transfers).

**This engine is the only approval control for documents this API creates.** Posting through the SAP
Service Layer bypasses B1's own approval procedures entirely, so a document that skips this engine
reaches SAP unapproved. Documents raised in the B1 client can still be held by SAP's own procedure;
[Credit Note Approvals (SAP)](#50-credit-note-approvals-sap) reads and decides those through SAP's
`ApprovalRequests` rather than mirroring them here.

---

### 45. Fiscal Device Offline Leases

**Base route:** `/api/fiscal-devices`  
**Auth:** Bearer + the `AdminOnly` policy

Which handset is allowed to sign receipts for a fiscal device while it is offline. Distinct from the
lease a handset draws for itself at `GET /api/vansales/fiscal/lease` — this is the administrative
view of the same arrangement.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/fiscal-devices/offline-leases` | Every device's offline lease |
| GET | `/api/fiscal-devices/{deviceId}/offline-lease` | One device's |
| PUT | `/api/fiscal-devices/{deviceId}/offline-lease` | Assign or reassign it |
| GET | `/api/fiscal-devices/handsets` | Active van accounts, and the device each already carries |
| GET | `/api/fiscal-devices/{deviceId}/preview` | What the platform says a device is, and whether it may be given to a van |
| PUT | `/api/fiscal-devices/{deviceId}/handset` | Register the handset that signs as this device, or release it |

Registration and nomination are two steps, in that order: a device nobody carries has nobody to
nominate. `preview` answers for device ids this application has never seen — that is the point of it,
since a device being registered for the first time is by definition not one it knows — and reports
what would refuse the registration rather than only whether it passes. `handsetUserId` on `preview` is
optional; without it the device is judged on its own merits, which is what the screen needs while
someone is still typing an id.

The refusal that matters is the operating mode. An `Online` device is one whose receipt sequence FDMS
owns, so a handset signing its own receipts into it forks the chain — that is a server device, not a
van's. Also refused: this server's own `Fiscalisation:DefaultDeviceId`, an expired certificate, a
device another handset already carries, an inactive or non-van account, and a device the platform
cannot describe.

`PUT .../handset` with a null `handsetUserId` releases the device instead of registering it, and
clears its nomination with it. That is the only way a device leaves a handset, which is why it is
where the outgoing van's queue is checked: it answers **409** when that handset is still carrying
signed receipts, or has never said whether it is, and `?force=true` goes through — the same guard as
moving a nomination, for the same reason.

See [Fiscalisation](#32-fiscalisation) for the platform this signs against, and
[Fiscalisation Console](#45a-fiscalisation-console) for the read-only view of devices already in use.

---

### 45a. Fiscalisation Console

**Base route:** `/api/fiscalisation-console`  
**Auth:** Bearer + the `AdminOnly` policy

Read-only. What an operator needs to answer "is anything owed to ZIMRA, and what is stuck" without
reading three pages and a log. Backs `/fiscalisation` in the web app.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/fiscalisation-console/devices` | Per device: operating mode, certificate expiry, fiscal day and hours elapsed against the taxpayer's limit, last receipt numbers, offline-signing holder, receipts not yet handed to the platform |
| GET | `/api/fiscalisation-console/work-queue` | Documents and van sales eligible for or failed at fiscalisation, filtered server-side |
| GET | `/api/fiscalisation-console/fiscal-days` | Per device per day: how far the close-package-submit sequence got, and where it stopped |

The work queue is filtered in the query rather than after the fetch, unlike the fiscal-status filter
on `/api/invoices` — a queue that only sees one page of results cannot tell an operator whether
anything is outstanding.

---

### 46. Batches

**Base route:** `/api/Batch`  
**Auth:** Bearer + `ApiAccess`

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Batch/search` | Find batches by number (`term`, required) |
| PATCH | `/api/Batch/{batchEntryId}/status` | Change a batch's status |

Batch *availability* for a document line is elsewhere:
`GET /api/Invoice/{itemCode}/batches/{warehouseCode}` and
`GET /api/Product/warehouse/{warehouseCode}/item/{itemCode}/batches`.

---

### 47. App Version

**Base route:** `/api/AppVersion`  
**Auth:** anonymous on the policy route, `AdminOnly` on the settings

What a mobile build is told about itself: whether it may keep running, and whether an update is
required or merely offered.

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/api/AppVersion/mobile` | **anonymous** | The version policy for the calling build |
| GET | `/api/AppVersion/mobile/settings` | **AdminOnly** | The stored policy (`appId`) |
| PUT | `/api/AppVersion/mobile/settings` | **AdminOnly** | Update it |

`GET /api/AppVersion/mobile` identifies the caller from the `X-App-Id`, `X-App-Platform` and
`X-App-Version` **headers**, falling back to the `appId`, `platform` and `currentVersion` query
parameters. It is anonymous on purpose: a build that has been locked out still has to be able to ask
why, and a build too old to authenticate is exactly the one that needs the answer.

---

### 48. Purchasing Documents

**Auth:** Bearer + `ApiAccess`, plus the permission below

Four SAP purchasing documents on four controllers, all shaped the same way: list, fetch one by
`docEntry`, create. [Purchase Orders](#14-purchase-orders) is the fifth and has more to it.

| Document | Base route | View | Create |
|----------|-----------|------|--------|
| Purchase request | `/api/PurchaseRequest` | `purchasing.requests.view` | `purchasing.requests.create` |
| Purchase quotation | `/api/PurchaseQuotation` | `purchasing.quotations.view` | `purchasing.quotations.create` |
| Goods receipt PO | `/api/GoodsReceiptPurchaseOrder` | `purchasing.grpo.view` | `purchasing.grpo.create` |
| Purchase invoice | `/api/PurchaseInvoice` | `purchasing.invoices.view` | `purchasing.invoices.create` |

Each one answers:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/PurchaseRequest` | List (`page` 1, `pageSize` 20, `fromDate`, `toDate`) |
| GET | `/api/PurchaseRequest/{docEntry}` | One document |
| POST | `/api/PurchaseRequest` | Create. Answers `201` |
| GET | `/api/PurchaseQuotation` | List (`page` 1, `pageSize` 20, `cardCode`, `fromDate`, `toDate`) |
| GET | `/api/PurchaseQuotation/{docEntry}` | One document |
| POST | `/api/PurchaseQuotation` | Create. Answers `201` |
| GET | `/api/GoodsReceiptPurchaseOrder` | List (`page` 1, `pageSize` 20, `cardCode`, `fromDate`, `toDate`) |
| GET | `/api/GoodsReceiptPurchaseOrder/{docEntry}` | One document |
| POST | `/api/GoodsReceiptPurchaseOrder` | Create. Answers `201` |
| GET | `/api/PurchaseInvoice` | List (`page` 1, `pageSize` 20, `cardCode`, `fromDate`, `toDate`) |
| GET | `/api/PurchaseInvoice/{docEntry}` | One document |
| POST | `/api/PurchaseInvoice` | Create. Answers `201` |

There is no update and no delete on any of the four. `/api/PurchaseRequest` is the one that takes no
`cardCode` filter — a request names what is wanted, not who it will be bought from.

---

### 49. Van Sales Customer Ordering

Orders van sales customers place for themselves in the Kefalos Orders Android app, replacing the
free-text WhatsApp messages someone used to read and retype.

The intake is deliberately **standalone**: a customer's order lives in `VanSalesOrders` and never
touches `SalesOrders` until staff explicitly convert it. That table feeds the SAP posting jobs and
the staff reports, and letting an unvetted customer-facing channel write into it would mean auditing
every existing query in the system for "is this row one a shopkeeper typed?".

#### Customer sign-in

**Base route:** `/api/van-sales-customer/auth`
**Auth:** none on the first four - a customer has no session yet, and refresh exists precisely to be
callable once the access token has expired. Rate limited under the `auth` policy.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/van-sales-customer/auth/login` | Exchange a phone number and its password for a session |
| POST | `/api/van-sales-customer/auth/otp/request` | Send a sign-in code to a phone number |
| POST | `/api/van-sales-customer/auth/otp/verify` | Exchange a code for a session |
| POST | `/api/van-sales-customer/auth/token/refresh` | Rotate the refresh token for a new session |
| POST | `/api/van-sales-customer/auth/logout` | End this device's session (requires a session) |

`login` is what the Kefalos Orders app uses. It takes `phoneNumber`, `password`, and the optional
`deviceId`/`deviceName` pair, and returns the same session body `otp/verify` returns. The password is
set by back-office staff when the shop is given access, stored as a BCrypt hash, and never returned
by any endpoint.

An unregistered number, an account with no password, and a wrong password are **one refusal**:
`VanSalesCustomerAuth.InvalidCredentials`, with the same wording and - because a verification runs
against a decoy hash when there is no account - the same time on the clock. Telling them apart would
name both the shops that trade with us and the accounts that cannot yet sign in.

The code endpoints remain for accounts that have no password and as the way back in when one is
forgotten. `otp/request` answers **200 with the same body for every well-formed number**, registered
or not, and the resend cooldown is a silent no-op rather than an error. Any observable difference
between a known and an unknown number would turn that endpoint into a way to read a supplier's
customer list one number at a time. `retryAfterSeconds` and `expiresInSeconds` come from
configuration, not from what happened.

Codes are delivered over **WhatsApp** through the OpenWA gateway - the channel these customers
already use - and are stored only as a keyed HMAC. A six-digit code has a million possibilities, so
what protects an account is the cap on attempts and the account lockout, not the code.

Failures of either kind spend **one** budget: the account's consecutive-failure counter, which locks
the account when it fills. Two counters would let an attacker use whichever credential still had
attempts left.

#### The customer's own surface

**Auth:** Bearer + the `VanSalesCustomerAccess` policy, which requires the `VanSalesCustomer` role and
a customer-code claim. That role is deliberately absent from `ApiAccessRoles`, so a customer token is
refused by every staff endpoint - including ones not yet written.

Every action resolves the customer **from the token**. Nothing in a body or a route identifies whose
order it is; an id a caller can supply is an id a caller can change.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/van-sales-customer/profile` | The shop, its route, calling days, next delivery and cut-off |
| GET | `/api/van-sales-customer/catalogue` | Priced items with a stock band. Honours `If-None-Match` |
| POST | `/api/van-sales-customer/devices` | Register this handset's push token |
| POST | `/api/van-sales-customer/orders` | Place an order |
| GET | `/api/van-sales-customer/orders` | Order history (`page` 1, `pageSize` 20) |
| GET | `/api/van-sales-customer/orders/{orderId}` | One of the caller's own orders |
| GET | `/api/van-sales-customer/orders/by-client-request/{clientRequestId}` | Did this key already produce an order? |
| POST | `/api/van-sales-customer/orders/{orderId}/cancel` | Withdraw an order before its cut-off |

`POST /orders` is **idempotent on `clientRequestId`** - a GUID the app mints when the draft is
created, not when it is sent. Sending the same key again returns the original order with `200` rather
than creating a second one or reporting a conflict: a handset that never saw the first reply is not
in error, and a `409` would make it retry forever. A replay carrying different lines still returns
the original; the key identifies the order, not the payload.

`by-client-request` is the reconciliation an offline app depends on. After a submit whose reply was
lost, `404` means no order exists and it is safe to send again.

The request carries **no prices**. The app shows a cached catalogue that may be days old; the server
prices against the current list and returns the priced order.

Stock is a **band** (`Unknown`, `InStock`, `Low`, `OutOfStock`), never a quantity - a depot figure
taken the afternoon before loading is not a promise, and what a supplier holds is not a customer's
business. An out-of-stock item is still accepted: orders are auto-accepted and the rep adjusts at
delivery, so refusing would throw away demand the depot may restock before the van loads.

#### Operator: accounts

**Base route:** `/api/van-sales-customer-accounts`
**Auth:** Bearer + `ApiAccess`

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/van-sales-customer-accounts` | List sign-ins (`routeCustomerId`, `includeInactive`) |
| POST | `/api/van-sales-customer-accounts` | Give a customer a sign-in, or re-point one at a new handset |
| POST | `/api/van-sales-customer-accounts/{accountId}/deactivate` | Withdraw a sign-in |

There is no self-registration: a customer who could sign themselves up could order as a shop they do
not own, and the rep visiting the shop is the only party able to confirm otherwise. Deactivating
**revokes the refresh tokens and push registrations in the same operation** - clearing the flag alone
would leave a lost handset signed in for the ninety days its token was issued for.

The POST carries the shop's `password`, which is **required for a sign-in that does not exist yet**
and optional for one that does: blank keeps the password already set, anything else replaces it. That
replacement is the reset path - a shop that has forgotten its password is re-onboarded by the rep
standing in it, which is the same check that justified creating the account. No endpoint reads a
password back, so an operator who loses one sets a new one.

#### Operator: the van's load list

**Base route:** `/api/van-sales-orders`
**Auth:** Bearer + `ApiAccess`, plus the permission below

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/van-sales-orders/route-load` | `salesorders.view` | What a van has been asked to carry |
| POST | `/api/van-sales-orders/{orderId}/delivery` | `salesorders.edit` | Record what was actually delivered |
| POST | `/api/van-sales-orders/{orderId}/convert` | `salesorders.create` | Turn a customer's order into a sales order |

`route-load` takes `assignedBusinessPartnerCode`, `routeCode`, `visitDate` and `status`, and returns
two views of the same orders: per-item totals for the depot to load to, and the orders themselves for
the door. It defaults to open orders only - a cancelled or delivered order on a load list is stock
loaded for nobody.

`delivery` **derives** the resulting status from the quantities rather than taking one: everything
delivered is `Fulfilled`, some is `PartiallyFulfilled`, none is `Expired`. A line left out of the
request is untouched, not zeroed, so a rep recording the one line they were short on does not thereby
declare the rest undelivered. Delivering more than was ordered is refused - extra goods handed over
at the door are a sale that belongs on an invoice, not inflated onto the order the customer can see.

`convert` is the single crossing into the SAP-bound pipeline, and always a person's decision. The
sales order it creates carries `SalesOrderSource.VanSalesCustomer` and lands as **Draft** for the
normal approval flow rather than auto-posting; credit is enforced here, where whoever is converting
can act on it.

---

### 50. Credit Note Approvals (SAP)

**Base route:** `/api/credit-note-approvals`  
**Auth:** Bearer + permissions as noted

A/R credit memos raised in the SAP B1 client and held by **SAP's own approval procedure**. SAP is the
source of truth: the list is read live from `ApprovalRequests` and the `Drafts` they hold, a decision is
a `PATCH ApprovalRequests(code)` recorded as the service approver (`SAP:ApprovalApproverUsername`, else
`SAP:Username`) with the caller named in the remarks, and the add is `DraftsService_SaveDraftToDocument`.
Nothing is mirrored into the local approval engine (§44), which governs documents this API posts.

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/credit-note-approvals` | `creditnotes.approve` or `creditnotes.add_approved` | The held requests: `status` open (default), pending, approved or all; `page` 1, `pageSize` 25, `beforeCode` for cursor paging |
| GET | `/api/credit-note-approvals/{code}` | either | One request: draft header and lines, attachments, approver lines, current stage |
| GET | `/api/credit-note-approvals/{code}/attachments/{lineNum}/download` | either | The bytes of one attached file, streamed from SAP |
| POST | `/api/credit-note-approvals/{code}/decision` | `creditnotes.approve` | Approve or reject: `{ "decision": "Approved" or "NotApproved", "remarks": "…", "clientRequestId": "…" }` |
| POST | `/api/credit-note-approvals/{code}/add` | `creditnotes.add_approved` | Convert the approved draft into the credit note, then project and fiscalise it |

**Paging the queue.** There are two ways, and they are not equivalent. `page` offsets from the top,
which is fine for a single read. `beforeCode` — the previous answer's `nextCursor` — continues below
the last row that answer carried, and is the one to use when walking the queue: `ApprovalRequests` is
ordered `Code desc` and it is live, so every credit memo raised while somebody pages takes the highest
Code yet, lands above everything they have read, and pushes one row they have already seen onto their
next offset page while burying another they never see. A cursor names where to carry on instead of
counting in from a top that has moved. `nextCursor` is null when the page is the end of the queue;
`totalCount` is always of the whole status set, never of what is below the cursor, and `page` is
carried through for the range label only. The approvals screen pages this way and keeps one cursor per
page reached so Previous is a re-read of the same window rather than a fresh count.

Each row says what may happen next: `canDecide` when the request is pending and SAP's current stage
lists the service approver, `canAdd` when SAP shows it approved and the draft is still open, and
`statusNote` in a sentence otherwise. A decision or an add that SAP refuses comes back as
`400 CreditNoteApproval.SapRejected` carrying SAP's own message; one that got no clear answer comes back
as `CreditNoteApproval.DecisionUncertain` / `AddUncertain`, and the request should be reloaded before
trying again. Both POSTs own their idempotency: a decision repeated with the same `Idempotency-Key` (or
`clientRequestId`) replays the first answer, and a draft is added at most once whoever clicks.

The credit note is fiscalised right after the add — a document added through the Service Layer never
passes the fiscalisation platform's B1 print bridge — unless `CreditNoteApprovals:FiscaliseAfterAdd` is
off. A fiscal failure is an Exception Center incident; the add still stands.

**SAP prerequisite:** the service approver must be listed as an approver on every stage of every SAP
approval template covering A/R credit memos, or every decision is refused with
`CreditNoteApproval.ApproverNotOnStage`.

---

### 51. Shops

**Base route:** `/api/Shops`  
**Auth:** Bearer, `[Authorize(Roles = "Admin")]` on the whole controller

The retail shop master. A shop holds the three values a till sells on — the business partner its
sales are invoiced to, the warehouse its stock leaves, and the cost centre its takings book against —
and every `TillOperator` account assigned to it inherits all three.

Administrator-only throughout, and not merely as tidiness: a shop's warehouse decides both what its
tills sell from and **which sales its operators may read**, so editing one changes who can see whose
money. See `/api/DesktopIntegration/sales` in [Desktop Integration](#30-desktop-integration) for the
read side.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Shops` | List shops; closed ones excluded unless asked for |
| GET | `/api/Shops/{id}` | One shop |
| POST | `/api/Shops` | Open a shop |
| PUT | `/api/Shops/{id}` | Change its name, business partner, warehouse or cost centre |
| PUT | `/api/Shops/{id}/active` | Close a shop, or reopen one |

##### GET `/api/Shops`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `includeInactive` | `false` | Bring back closed shops too; they still own their sales history |

**Response:** `List<ShopDto>` — `id`, `code`, `name`, `businessPartnerCode`, `warehouseCode`,
`costCentreCode`, `isActive`, `assignedOperatorCount`, `createdAt`, `updatedAt`.

`assignedOperatorCount` counts **active** accounts only. A disabled account cannot sell, so it is not
something closing the shop would strand.

##### POST `/api/Shops`

**Body:** `CreateShopRequest`.

```json
{
  "code": "MACHIPISA",
  "name": "Machipisa",
  "businessPartnerCode": "C00123",
  "warehouseCode": "CORMACH2",
  "costCentreCode": "CC-MACH"
}
```

`costCentreCode` is optional — SAP defaults a missing one, so requiring it would stop an otherwise
correctly configured shop from trading over a reporting dimension. The other four are required.

**Response:** `ShopDto`. `409 Conflict` on a duplicate `code`, and `409` again if another shop
already uses that `warehouseCode` — including a **closed** one. Each shop needs its own warehouse
because the warehouse is what scopes an operator's view of the day's takings; two shops sharing one
would show each other's sales to both, and a closed shop still owns the history behind its warehouse.

##### PUT `/api/Shops/{id}`

**Body:** `UpdateShopRequest` — `name`, `businessPartnerCode`, `warehouseCode`, `costCentreCode`.

Carries no `code` and no `isActive`. The code is what sales history and reporting group on, so a shop
needing a different one is a new shop; `isActive` has its own endpoint because closing has a rule
attached that an edit form silently flipping a checkbox would walk past.

**Response:** `ShopDto`. `404` for an unknown id, `409` for a warehouse another shop holds.

##### PUT `/api/Shops/{id}/active`

| Parameter | Notes |
|-----------|-------|
| `isActive` | `false` closes the shop, `true` reopens it |

**Response:** `ShopDto`. `409 Conflict` when closing a shop that still has active till operators
assigned, naming how many — their accounts would keep authenticating and then fail at the first sale
with a refusal naming the shop, which reads to an operator as a broken till rather than a closed one.
Reopening runs no such check: it strands nobody. Setting the state it already has is a no-op rather
than an error, so a double-click is not something an administrator has to read and dismiss.

There is no delete. A shop owns its sales history, and its warehouse stays reserved after it closes so
that history cannot be handed to a new shop.

---

## DTOs Reference

### Batch Allocation

Used when creating invoices with batch-managed items.

**Batch Allocation Request:**

```json
{
  "lines": [
    {
      "lineNumber": 0,
      "itemCode": "PRD001",
      "warehouseCode": "WH01",
      "quantity": 10,
      "uoMCode": "Each",
      "batchAllocations": [
        { "batchNumber": "B2026-001", "quantity": 6 },
        { "batchNumber": "B2026-002", "quantity": 4 }
      ]
    }
  ],
  "autoAllocate": true,
  "strategy": "FEFO"
}
```

**Allocation Strategies:**
- `FEFO` — First Expiry, First Out (default for perishable goods)
- `FIFO` — First In, First Out (by admission date)
- `Manual` — Client specifies exact batch allocations

**Batch Allocation Result:**

```json
{
  "isValid": true,
  "validationErrors": [],
  "warnings": [],
  "allocatedLines": [
    {
      "lineNumber": 0,
      "itemCode": "PRD001",
      "warehouseCode": "WH01",
      "totalQuantityAllocated": 10,
      "batches": [
        {
          "batchNumber": "B2026-001",
          "quantityAllocated": 6,
          "availableBeforeAllocation": 80,
          "remainingAfterAllocation": 74,
          "expiryDate": "2027-01-15",
          "allocationOrder": 1
        },
        {
          "batchNumber": "B2026-002",
          "quantityAllocated": 4,
          "availableBeforeAllocation": 50,
          "remainingAfterAllocation": 46,
          "expiryDate": "2027-06-01",
          "allocationOrder": 2
        }
      ]
    }
  ],
  "totalLinesValidated": 1,
  "linesPassedValidation": 1,
  "batchesAutoAllocated": 2,
  "strategyUsed": "FEFO"
}
```

### Stock Validation Error

Returned when stock is insufficient for an operation:

```json
{
  "lineNumber": 0,
  "itemCode": "PRD001",
  "itemName": "Widget A",
  "warehouseCode": "WH01",
  "requestedQuantity": 100,
  "availableQuantity": 45,
  "shortage": 55,
  "batchNumber": null,
  "message": "Insufficient stock: requested 100, available 45"
}
```

### Webhook Event Type Info

```json
{
  "eventType": "stock.low",
  "category": "Stock",
  "description": "Triggered when stock falls below reorder level"
}
```

### SAP Connection Status

```json
{
  "isConnected": true,
  "status": "Connected",
  "lastConnectedAt": "2026-04-01T10:00:00Z",
  "lastErrorAt": null,
  "lastError": null,
  "consecutiveFailures": 0,
  "responseTimeMs": 120,
  "sapVersion": "10.0",
  "companyDb": "SBO_Production"
}
```

---

## Error Codes

| HTTP Status | Meaning |
|-------------|---------|
| 200 | Success |
| 201 | Created |
| 400 | Bad Request / Validation Error |
| 401 | Unauthorized (missing or invalid token) |
| 403 | Forbidden (insufficient permissions) |
| 404 | Resource Not Found |
| 409 | Conflict (duplicate idempotency key or concurrency issue) |
| 429 | Too Many Requests (rate limited) |
| 500 | Internal Server Error |
| 502 | SAP Service Layer Unavailable |
| 503 | Service Unavailable |

---

## Currencies

The system operates with dual currencies:

| Code | Name |
|------|------|
| `USD` | United States Dollar |
| `ZIG` | Zimbabwe Gold |

Exchange rates are managed via the `/api/ExchangeRate` endpoints and are used for currency conversion across all financial documents.
