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
- Changes a caller has to act on are recorded in [CHANGELOG.md](CHANGELOG.md), including endpoints kept working under an older name

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
  - [SAP User Accounts](#29a-sap-user-accounts)
  - [Desktop Integration](#30-desktop-integration)
  - [Customer Portal](#31-customer-portal)
  - [Fiscalisation](#32-fiscalisation)
  - [REVMax](#32a-revmax)
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
  - [Market Breakages](#52-market-breakages)
  - [Stock Write-offs](#53-stock-write-offs)
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

`POST /api/SalesOrder` requires a key on every create, whatever the order's source. The middleware
lets merchandiser, sales rep, ADR and sales roles through without an `Idempotency-Key` header,
because their older clients send `clientRequestId` in the body instead — and the controller folds
the header into that field before validation, so either one satisfies the rule. A request carrying
neither is refused with **400**, naming both ways to supply it.

### Endpoints that replay the real document

These do their own durable idempotency in the handler, which persists the response and replays the
actual document on a repeated key:

| Endpoint | Key carrier |
| --- | --- |
| `POST /api/Invoice` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/CreditNote` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/CreditNote/from-invoice/{invoiceId}` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/IncomingPayment` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/InventoryTransfer` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/Quotation` | `clientRequestId` body field (or `Idempotency-Key` header) |
| `POST /api/crates/transactions/{id}/pods` | `clientRequestId` form field (or `Idempotency-Key` header) |
| `POST /api/crates/transactions/{id}/grvs` | `clientRequestId` form field (or `Idempotency-Key` header) |

`IdempotencyMiddleware` deliberately stands aside for these routes, because its own in-memory replay
would short-circuit the request and answer with a bare message instead of the document. Ownership is
per exact route, not per controller: every other route under the same controllers still relies on
the middleware.

Incoming payments are the route that needs this most, and the only one on the list with no key of
its own in SAP: `clientRequestId` is not forwarded to the Service Layer and there is no lookup by
reference, so the stored response is the only way a caller that lost its reply can learn the payment
exists rather than sending it a second time. Inventory transfers and quotations also look their key
up among their own records, so a resubmission is answered from those even after the key expires.

### Invoice creation: the key is written into SAP

`POST /api/Invoice` is the strictest case in the system, because a duplicate is a second fiscal
receipt that cannot be withdrawn from ZIMRA. An invoice that names no `U_Van_saleorder` of its own
is posted carrying one derived from the caller's key — `WEB-<key>`, or `WEB-<fingerprint>` when the
key is too long or carries characters SAP will not take — so the document can be found in SAP
afterwards by anyone holding the key, this server included. `clientRequestId` itself is never sent
to SAP, which is why the derived reference exists at all.

A caller may still supply its own `U_Van_saleorder`, and it is used as-is. It may not be one the
system generates for itself (`DS-`, `CONSOL-`, `WEB-`, or a reference belonging to a till sale):
those return **400 `Invoice.ReservedSaleReference`**.

Three answers to a retry, and they mean different things:

| Response | Meaning |
| --- | --- |
| **201** with the original body | The first attempt finished; this is its stored response, same `DocEntry`. |
| **200/201** "Invoice already exists" | SAP holds an invoice under this key. Nothing was posted again. |
| **409 `Idempotency.PostOutcomeUnknown`** | An earlier attempt sent a post whose outcome is not known, and SAP does not show the document yet. Nothing was sent. Retry shortly. |

A client that hangs up does not stop the post. Once the request has left for SAP it runs to
completion regardless of the caller — a closed tab, a proxy timeout, a navigation away — because an
invoice SAP has taken must not be one this side never learned the number of. If the reply is lost
anyway, the handler asks SAP on the key and returns the invoice it finds, so the caller is answered
with the document rather than sent to go and look for it. A request abandoned *before* the post is
simply dropped, and nothing is sent.

The last one is a wait, not a failure. A post whose reply was lost — a timeout, a dropped
connection — leaves its claim standing deliberately, because SAP may hold the invoice and simply not
be showing it yet; the retry asks SAP rather than posting again. Once SAP shows it, the retry is
handed that invoice. If SAP still shows nothing after
`Security:IdempotencyUnresolvedPostGraceMinutes` (default 15), the post never landed and the retry
posts normally. A refusal from SAP releases the claim immediately, so a document that only needs
fixing stays retryable under the same key.

### Credit notes: the same key, in NumAtCard

A credit note is the strictest case of all — a duplicate is a second ZIMRA credit receipt against
one return — and until recently it reached SAP carrying nothing that identified it. Both create
routes now write `CN-{key}` (or `CN-{fingerprint}` for a long or awkward key) into the credit note's
**`NumAtCard`**, derived from the caller's idempotency key, and ask SAP about that reference before
posting anything. `NumAtCard` rather than a UDF because it is a standard field on every marketing
document; this system writes nothing else into it, and a caller cannot set it.

The retry answers match the invoice route, with one difference: **409 `Idempotency.PostOutcomeUnknown`**
here means an earlier attempt's outcome is unresolved and nothing was sent again. Once
`Security:IdempotencyUnresolvedPostGraceMinutes` has passed the retry proceeds, and SAP is asked
about the reference before anything is posted — so it adopts the credit note if one exists and
raises one only if none does.

Two related guards, both of which used to fail open:

- A credit note SAP refuses returns **400 `CreditNote.SapRejected`** and releases the key, so the
  document can be corrected and sent again under it. A failure that is *not* SAP's own answer — a
  gateway error, a timeout — keeps the key instead, because the credit note may exist behind it.
- `POST /api/CreditNote/from-invoice/{invoiceId}` refuses when SAP cannot be asked what the invoice
  has already been credited. It used to fall back to the local database, which does not hold a
  credit note whose reply was lost, and so cleared a second full credit note against the invoice.

The local record is written before fiscalisation is attempted, and neither the post nor the save
runs on the caller's connection: a client that hangs up mid-request no longer leaves a credit note
in SAP that this side has no record of.

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
  "status": 400,
  "detail": "The request contains validation errors.",
  "instance": "/api/whatsapp/sessions",
  "errors": {
    "FieldName": ["Error message"]
  },
  "code": "FieldName",
  "errorDetails": [
    { "code": "FieldName", "description": "Error message", "type": "Validation" }
  ],
  "traceId": "00-..."
}
```

`errors` is the contract — the RFC 9457 / ASP.NET dictionary of field name to messages, and the
only member a client should read messages out of. Everything alongside it is a convenience:

| Member | What it is |
|--------|------------|
| `code` | The first error's code, for a client that branches on one |
| `errorDetails` | Every error with its code and `ErrorType`, for logging and diagnostics |
| `traceId` | Correlates the response with the server log |

`errorDetails` is deliberately not called `errors`: it used to be, and because
`ProblemDetails.Extensions` is `[JsonExtensionData]` the response carried the key twice — once as
the dictionary and once as this array. A strict parser may reject such a document outright, and
`JsonDocument`, which does not, resolved the name to the array rather than the dictionary.

Refusals that are not validation failures (`404`, `409`, and the rest) carry no `errors` dictionary
at all. Read `detail` for the sentence to show, `code` to branch on, `errorDetails` for the list.

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
| POST | `/api/Invoice/{docEntry}/cancel` | `invoices.void` | Cancel the invoice, reversing it in full with a credit note |

**Query parameters for `/paged`:** `page` (1), `pageSize` (20), `docNum`, `cardCode`, `fromDate`,
`toDate`, `vanSalesOnly`

There is **no update and no delete route**: an invoice is created by posting it to SAP, and
correcting one is a credit note.

**Cancelling an invoice**

`POST /api/Invoice/{docEntry}/cancel` withdraws a posted invoice in full. It does not delete or
void anything in SAP — the document has been fiscalised and ZIMRA has seen it — it raises the
credit note that reverses every line at its full quantity, with the reason written to each line's
`U_Reasons` field, and fiscalises that credit note in turn.

```json
{ "reason": "Cancellation", "comments": "Customer collected nothing", "clientRequestId": "..." }
```

`reason` must be one of the values from `GET /api/CreditNote/reasons`; anything else is refused
before the credit note is built, with the allowed values named in the message. `clientRequestId`
(or an `Idempotency-Key` header) makes a retry replay instead of posting a second credit note.

The response carries the credit note it raised and `notifiedWarehouses`. Cancelling also pushes an
`InvoiceCancelled` event over the [notifications hub](#realtime) to `warehouse:{CODE}` — the till that issued
the receipt — and raises the stored notification everyone who works invoices sees. An empty
`notifiedWarehouses` means the invoice could not be traced to a till and nobody was pushed.

A partial credit is not a cancellation: use `POST /api/CreditNote/from-invoice/{invoiceId}`.

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
| GET | `/api/CreditNote/reasons` | `invoices.view` | The reasons SAP allows on a credit note line |
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

**Credit Note Types:** `Return`, `PriceAdjustment`, `Discount`, `Damaged`, `Other`, `Cancellation`  
**Credit Note Statuses:** `Draft`, `Pending`, `Approved`, `Cancelled`, `Applied`

`Cancellation` is set by `POST /api/Invoice/{docEntry}/cancel` and marks a credit note that
withdrew an invoice in full rather than took stock back off a sale that stands.

**Reasons**

`/api/CreditNote/reasons` answers SAP's own list, read live from the running company database:

```json
{ "reasons": [ { "value": "Cancellation", "description": "Customer order not collected" } ] }
```

These are the valid values of `U_Reasons`, a **line-level** user field on `RIN1`. SAP rejects any
value it does not define, and the list is not the same in every company database — production
carries the `Re-Invoice …` and `Cancellation …` entries that the test database has never had — so
neither the picker nor the payload may hard-code it. `value` is what goes on the line;
`description` is what a person reads. The API caches the list for six hours.

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
| GET | `/api/credit-control/headroom` | `customers.view` or `salesorders.approve` | How much credit room named customers have left |

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
| GET | `/api/PurchaseOrder/documents` | `purchasing.view` or `salesorders.view` | Attached documents (`poReferenceNumber`) |

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

**Daily incoming payment for desktop sales**

**Base route:** `/api/daily-incoming-payment-settings`
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/daily-incoming-payment-settings` | Whether desktop sales get their 17:00 daily incoming payment, or post invoices only |
| PUT | `/api/daily-incoming-payment-settings` | Turn it on or off: `{ "enabled": false }` |

Both answer `{ "enabled": false, "updatedAtUtc": "2026-09-18T10:00:00Z" }`; `updatedAtUtc` is null until
someone has saved it, while `DesktopSalePosting:DailyPaymentEnabled` (off) still decides. The switch is a
`SystemConfigs` row the `daily-incoming-payment` job reads on every run, so a change needs no restart.
Turning it back on pays invoices up to `DailyPaymentLookbackDays` (7) old. Web → Settings → Payments sets it.

**Daily incoming payments and their G/L accounts**

**Base route:** `/api/daily-incoming-payments`
**Auth:** Bearer + Admin, Manager or Cashier role to read; Admin to change a mapping

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/daily-incoming-payments?from=2026-09-14&to=2026-09-20&cardCode=CIS006&status=Posted` | The daily payments for a range of trading days, newest first. `cardCode` and `status` are optional |
| GET | `/api/daily-incoming-payments/{id}` | One payment with the invoices it settles and the till, van or consolidated sale behind each |
| GET | `/api/daily-incoming-payments/gl-mappings` | Every partner's cash and electronic G/L accounts, run (`Shops` 17:00 / `Vans` 20:00) and email recipients |
| PUT | `/api/daily-incoming-payments/gl-mappings/{cardCode}` | Create or replace a partner's mapping: `{ "cashAccount": "700300", "electronicAccount": "701100", "run": "Shops", "notifyEmails": "a@x.com, b@x.com", "isActive": true }`. Both accounts are checked in SAP first |

Cash posts to the cash account; Ecocash, Innbucks and swipe post to the electronic account as transfer
money. A partner with no active mapping is held (`Pending`, "No active G/L mapping") rather than posted
to SAP's default account. Every payment's `DAYPAY-yyyyMMdd-CARDCODE` reference is also its SAP
`CounterReference` and `JournalRemarks`. Once it posts, the mapping's recipients are emailed the totals
and the invoice list.

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
| PATCH | `/api/InventoryTransfer/request/{docEntry}` | Change an open request's lines and warehouses. Admin, StockController, WashBay, DepotController, Manager |
| POST | `/api/InventoryTransfer/request/{docEntry}/convert` | Authorize a request and generate the SAP transfer. Admin, StockController, WashBay, DepotController |
| POST | `/api/InventoryTransfer/request/{docEntry}/close` | Close a request in SAP without converting it. Admin, StockController, WashBay, DepotController |
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
| POST | `/api/InventoryTransfer/pending/{id}/decision` | Approve or reject. Admin, StockController, WashBay, DepotController, Manager |
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
| GET | `/api/Report/negative-stock-trend` | Stock SAP is holding below zero, day by day (`days`, default 30) |
| GET | `/api/Report/sales-summary` | Sales summary for a date range |
| GET | `/api/Report/top-products` | Top selling products |
| GET | `/api/Report/top-customers` | Top customers |
| GET | `/api/Report/slow-moving-products` | Products with no movement (`daysThreshold`, default 30) |
| GET | `/api/Report/stock-summary` | Stock on hand **and its value**, optionally for one warehouse |
| GET | `/api/Report/stock-movement` | Stock movement over a date range |
| GET | `/api/Report/low-stock-alerts` | Low stock alert items (`warehouseCode`, `threshold`) |
| GET | `/api/Report/receivables-aging` | Customer aging analysis |
| GET | `/api/Report/payment-summary` | Payments over a date range |
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

There is no separate webhook permission to delegate. Managing a webhook decides where company data
is sent, so it stays with `system.admin`.

**Event types.** Every type below is accepted in a subscription, but not every type is published.
A subscription to one marked *Not published* is stored and looks healthy, and receives nothing —
check this table before relying on an event. `GET /api/Webhook/event-types` returns the same
information in each type's `description`.

| Event | Status | Published when |
|-------|--------|----------------|
| `invoice.created` | Live | An invoice is created in SAP by a known user: through the API, desktop sales consolidation or a stock reservation. An invoice no user can be attributed to raises nothing. |
| `invoice.cancelled` | Live | An invoice is cancelled and reversed by a credit note. |
| `stock.transfer` | Live | SAP accepts an inventory transfer created by this system, by any route — market breakages moving to the returns warehouse included. |
| `inventory.received` | Live | A goods receipt PO is created in SAP. Receiving against this system's own purchase orders does not raise it; those never reach SAP. |
| `inventory.adjusted` | Live | A stock write-off posts to SAP as a goods issue. |
| `sap.sync.success` | Live | A SAP-backed cache (warehouses, business partners, G/L accounts) refills. Fires on every refill — several times an hour under normal load. |
| `sap.sync.failed` | Live | A SAP-backed cache refill fails. |
| `sap.connection.lost` | Conditional | The SAP health check goes from reachable to unreachable. Only while `SystemHealthAlert:Enabled` is true. |
| `sap.connection.restored` | Conditional | The SAP health check is reachable again after a `lost`. Only while `SystemHealthAlert:Enabled` is true. |
| `payment.received` | Conditional | A payment gateway confirms a payment. Only while a gateway (PayNow, Innbucks, Ecocash) is enabled. |
| `payment.failed` | Conditional | A payment gateway reports a failed payment. Same condition. |
| `payment.refunded` | Conditional | A payment is refunded through a gateway. Same condition. |
| `invoice.paid` | Not published | — |
| `stock.low` | Not published | — |
| `stock.out` | Not published | — |
| `stock.replenished` | Not published | — |
| `customer.created` | Not published | — |
| `customer.updated` | Not published | — |

**Create Webhook Request:**

```json
{
  "name": "Invoice feed",
  "url": "https://hooks.example.com/invoices",
  "secret": "whsec_abc123",
  "events": ["invoice.created", "invoice.cancelled"],
  "retryCount": 3,
  "timeoutSeconds": 30,
  "customHeaders": {
    "X-Custom-Header": "value"
  }
}
```

**Delivery.** Each delivery is a `POST` of this body:

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "event": "invoice.created",
  "timestamp": "2026-09-21T10:30:00Z",
  "data": { }
}
```

`data` carries the event's own fields. With these headers:

| Header | Value |
|--------|-------|
| `Content-Type` | `application/json` |
| `X-Webhook-Event` | The event type |
| `X-Webhook-Delivery-Id` | A new GUID for **each attempt** |
| `X-Webhook-Signature` | `sha256=<hex>`: HMAC-SHA256 of the raw body, keyed with the webhook's secret. Sent only when a secret is set. |

The signature is a header, not a body field. Verify it against the raw bytes received, before
parsing.

**Retries and duplicates.** A response outside 2xx, or no answer within `timeoutSeconds`, is retried
after 2, 4, 8… seconds, up to `retryCount` attempts in all. So a receiver can see the same event more
than once. Deduplicate on the body's `id`: it is fixed per event and repeated on every retry and to
every subscriber. `X-Webhook-Delivery-Id` changes per attempt and cannot be used for that.

Every attempt is logged, and `GET /api/Webhook/deliveries` returns the log with each attempt's
response code and error.

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
| POST | `/api/RateLimit/reset/{clientId}` | `users.edit` | Clear a client: counter, window and block |
| POST | `/api/RateLimit/unblock/{clientId}` | `users.edit` | The same action under its original name |
| GET | `/api/RateLimit/blocked` | `users.edit` | Every client currently blocked |
| GET | `/api/RateLimit/stats` | `users.edit` | Totals across all clients |
| GET | `/api/RateLimit/config` | `users.edit` | The limits in force |
| PUT | `/api/RateLimit/config` | `users.edit` | Change them, without a restart — see below |
| POST | `/api/RateLimit/cleanup` | `users.edit` | Clear expired counters |

Rate limit administration is gated on `users.edit`, not on a rate-limit permission of its own —
there isn't one.

##### `reset/{clientId}` clears everything, and `unblock/{clientId}` is the same action

`requestCount` zeroed, the window restarted, and `isBlocked` / `blockExpiresAt` cleared — the
client can call again immediately.

The two paths are **one action with two routes**, not two implementations, so they cannot answer
differently. They used to be separate and had drifted: `reset` zeroed the counter and left the
block in place, so **resetting a blocked client left it blocked** — the one state anybody reaches
for reset in. An operator clearing a client and watching it stay shut out cannot tell a broken
endpoint from a client that is still hammering the API.

`reset` is the name to use. `unblock` stays because it is a version `1.0` route with clients that
may still call it, and this API keeps those working.

`totalBlockedCount` survives. It is the client's history rather than its current state, and it is
what says a client needs a conversation rather than another reset.

`404` on a client id that has no rate limit row, which is the answer for a client that has never
been counted — it does not create one.

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
| GET | `/api/sap-settings/connection` | Whether the API may send requests to SAP: `{ "enabled": true, "updatedAtUtc": null }` |
| PUT | `/api/sap-settings/connection` | Turn the SAP connection on or off: `{ "enabled": false }` |

**The SAP connection switch.** Off, every request to the Service Layer is refused before it is sent,
exactly as an open circuit breaker refuses it. Endpoints that post to SAP answer `503` (or queue the
document, where they already queue while the circuit is open), and the posting jobs skip their pass.
Queued invoices, transfers and payments post by themselves once it is back on. Stored in
`SystemConfigs` (`SAP.ConnectionEnabled`), so no restart is needed. The node that saves it applies it
at once, and every other node re-reads it within 15 seconds. `/health/dependencies` reports SAP as
`Degraded` while it is off. Unsaved, it is on. This is separate from the `SAP:Enabled` configuration
flag, which only decides whether some jobs are scheduled.

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

### 29a. SAP User Accounts

**Base route:** `/api/sap-users`
**Auth:** Bearer + `sapusers.view` / `sapusers.unlock` / `sapusers.change_password` (Admin holds all three)

The user accounts inside SAP Business One — the logins for the SAP client itself. These are **not**
this application's accounts, which are under [User Management](#12-user-management); a person may
hold one, the other, both or neither. Creating and removing SAP users stays in the B1 client,
because it is a licensing decision.

Both writes require the Service Layer account this application signs in as to be a SAP superuser.
SAP refuses them otherwise, and its refusal is passed through as the `detail` of the problem
details rather than translated.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sap-users` | The SAP user accounts, with the count of those locked out |
| POST | `/api/sap-users/{internalKey}/unlock` | Clear SAP's lock on one account |
| POST | `/api/sap-users/{internalKey}/password` | Set a new password on one account |

**Query parameters (GET):**

| Parameter | Type | Description |
|-----------|------|-------------|
| `search` | string | Matched against both `userCode` and `userName`; omit to read all |
| `lockedOnly` | bool | Only the accounts SAP is currently keeping out |

**List Response:**

```json
{
  "items": [
    {
      "internalKey": 12,
      "userCode": "kmoyo",
      "userName": "Kudzai Moyo",
      "email": "kudzai@example.com",
      "isLocked": true,
      "isSuperuser": false,
      "lastPasswordChangedBy": "manager",
      "lastLogoutDate": "2026-09-18T00:00:00Z"
    }
  ],
  "totalCount": 1,
  "lockedCount": 1,
  "truncated": false
}
```

`truncated` is true when SAP held more accounts than one read returns (200). The two counts then
describe the page in hand rather than the company, and the caller should narrow with `search`.

**Unlock:** no body. The account is read back from SAP after the write, so a 200 means the account
really is unlocked rather than that SAP accepted the PATCH. An account that was not locked is
refused with `409 SapUser.NotLocked` — whoever cannot sign in is failing on something else, and
answering "done" would send them round the same loop.

**Change Password Request:**

```json
{
  "newPassword": "Ch33se!2026"
}
```

The password is never logged, never audited and never echoed back; the response is the account, with
the `lastPasswordChangedBy` SAP now records against it. SAP's own password policy decides what it
will accept, and its reason ("Password must contain at least one digit") is what a refusal carries.

Both writes raise an audit row — `UnlockSapUser` / `ChangeSapUserPassword` against entity type
`SapUser` — naming the signed-in user, resolved from their account rather than from the request's
name claim.

---

### 30. Desktop Integration

**Base route:** `/api/DesktopIntegration`  
**Auth:** Bearer + ApiAccess  
**Audit:** every **write** is written to the audit log by `DesktopIntegrationAuditFilter`, outcome
included. Reads are not — a till polls stock and queue state for as long as it is switched on, and
those rows would bury the ones worth reading. The three reads that show a shop's takings,
`GET /sales`, `GET /sales/analysis` and `GET /end-of-day/report`, log for themselves in their handlers
instead.

This controller supports stock reservations and queue-based invoice posting for the desktop application.

The filter's row names the endpoint and how it answered. Six calls write a second, fuller row from
their handler, because their subject is in the request body and the endpoint alone cannot name it:
`POST /sales` (the sale, its customer, warehouse, money and receipt — and whether the answer was a
replay rather than a new sale), `POST /invoices` (the SAP document, or that the invoice was deferred
to the queue instead), `POST /invoices/queued`, `DELETE /queue/{ref}` and
`POST /queue/{ref}/retry` (the status the entry was cancelled or retried from, which the queue does
not keep), and `POST /end-of-day/consolidate` (how many sales became how many SAP invoices).
`POST /sales/{ref}/post` already logged its own.

`end-of-day/consolidate` is also run by `EndOfDayConsolidationJob`. That run is audited too, with no
username, because no user raised it.

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
| GET | `/api/DesktopIntegration/stock/{warehouseCode}/transfer-adjustments` | Transfers the stock ledger applied, one row per line with the document and time applied (`fromDate`, `toDate`: snapshot days, at most 92) |
| GET | `/api/DesktopIntegration/stock/monitored-warehouses` | Which warehouses are snapshotted |
| POST | `/api/DesktopIntegration/stock/fetch-daily` | Take today's snapshot now |
| POST | `/api/DesktopIntegration/stock/{warehouseCode}/refresh` | Move a shop warehouse's ledger to SAP's figure now, less unposted till sales — for a GRPO the ledger never saw. Writes no movement rows; 409 for vans or no snapshot today |

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
| GET | `/api/DesktopIntegration/transfers/warehouse/{warehouseCode}/date-range` | (class) | Transfers between two dates where the warehouse is either end of the header or of any line |
| POST | `/api/DesktopIntegration/transfer-requests` | (class) | Raise a transfer request |
| GET | `/api/DesktopIntegration/transfer-requests/items` | (class) | Items a till may request (SAP sales items, `OITM.U_SalesItem = 'Yes'`) |
| GET | `/api/DesktopIntegration/transfer-requests/{docEntry}` | (class) | One request |
| GET | `/api/DesktopIntegration/transfer-requests/warehouse/{warehouseCode}` | (class) | A warehouse's requests |
| GET | `/api/DesktopIntegration/transfer-requests/paged` | (class) | Requests, paginated |
| POST | `/api/DesktopIntegration/transfer-requests/{docEntry}/convert` | Admin, StockController, WashBay, DepotController | Authorise and generate the transfer |
| POST | `/api/DesktopIntegration/transfer-requests/{docEntry}/close` | Admin, StockController, WashBay, DepotController | Close without converting |
| GET | `/api/DesktopIntegration/transfer-queue` | (class) | The transfer queue (`sourceSystem`, `limit` 100) |
| GET | `/api/DesktopIntegration/transfer-queue/review` | (class) | Entries needing a look (`limit` 50) |
| GET | `/api/DesktopIntegration/transfer-queue/stats` | (class) | Queue counts |
| GET | `/api/DesktopIntegration/transfer-queue/{externalReference}` | (class) | One entry |
| POST | `/api/DesktopIntegration/transfer-queue/{externalReference}/retry` | (class) | Retry it |
| DELETE | `/api/DesktopIntegration/transfer-queue/{externalReference}` | (class) | Drop it |
| POST | `/api/DesktopIntegration/webhook/transfer-event` | (class) | Take a transfer event from SAP |
| GET | `/api/DesktopIntegration/transfer-listener/status` | Admin, Manager | Whether transfers are reaching local stock: the listener's SAP poll, its delivery queue, and what this API's ledger applied today (`recentDocumentCount` 20). Answers 200 with `reachable: false` when it is down |
| POST | `/api/DesktopIntegration/transfer-listener/check-now` | Admin, Manager | Make the listener poll SAP and replay waiting lines now rather than wait for its cycle; reports lines delivered, queued and still waiting |

#### Desktop sales and end of day

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/DesktopIntegration/sales/{reference}/credit-notes` | Saved fiscal credit notes for this sale, scoped to the caller's warehouse |
| GET | `/api/DesktopIntegration/sales/{reference}/credit-notes/prepare` | Read the original REVMax receipt and remaining creditable quantities; no SAP document required |
| POST | `/api/DesktopIntegration/sales/{reference}/credit-notes` | Persist and fiscalise a credit through REVMax using a permanent request key, reason and selected line quantities |
| POST | `/api/DesktopIntegration/sales/{reference}/credit-notes/{id}/continue` | Continue a saved credit only if submission has not started |
| POST | `/api/DesktopIntegration/sales/{reference}/credit-notes/{id}/reconcile` | Read back the saved credit's fiscal status without resubmitting |
| GET | `/api/DesktopIntegration/credit-notes` | Every desktop credit across all sales (till, vending, van), filtered by date, warehouse, source, fiscal and SAP status, with SAP-status counts and per-currency totals; scoped to the caller's warehouse |
| POST | `/api/DesktopIntegration/credit-notes/{id}/retry-sap` | Send a refused SAP credit memo again now and reset its attempt count (Admin, Manager) |
| POST | `/api/DesktopIntegration/credit-notes/{id}/mark-raised` | Record the memo a person raised by hand for a `ManualInSap` credit. Body `{ "sapDocNum": 91001 }`. SAP is checked first: the memo must exist, not be cancelled, be for the sale's customer, and not belong to another credit. The credit becomes `Posted`, and the ledger stops holding its returned units (Admin, Manager) |
| POST | `/api/DesktopIntegration/sales` | Record a desktop sale |
| GET | `/api/DesktopIntegration/sales` | The sales (`warehouseCode`, `cardCode`, `consolidationStatus`, `fromDate`, `toDate`, `page` 1, `pageSize` 50, `sourceSystem`, `search` — till reference, fiscal receipt number, customer code or name, or route customer code or name containing the text, ignoring case, or the sale number or SAP document number; a bare number is tried as both, and the prefixed form `INV10427` matches only the sale number; `sort` — `newest` (default), `oldest`, `total-desc`, `total-asc` or `customer`; `minTotal`/`maxTotal`, inclusive; `paymentDifference` — `any` (default), `exact`, `under` or `over`, comparing what was tendered with what was rung up; and the repeatable `warehouses`, `consolidationStatuses`, `fiscalizationStatuses`, `paymentMethods` and `sourceSystems`, which are the many-value forms of the filters above and are combined with them. `includeFacets=true` adds `facets` — for each of those five groups, every value and how many sales would match if it alone were selected, counted with that group's own selection lifted and every other filter still applied, with a blank column reported as `(blank)`, which the matching filter accepts — and `unfilteredCount`, what the period holds before any of them; both are absent (zero and null) otherwise. A shop-scoped caller naming any warehouse but their own is refused rather than narrowed, whichever form they use). Each row carries `saleNumber`, the short number the till prints on the customer receipt and the one a person names the sale by; `createdBy` (the account id the till captured it under) and `createdByName`, that account's holder, null when the id names no account; and `routeCustomerId`, `routeCustomerCode` and `routeCustomerName`, the van shop or vending vendor who bought, as the sale recorded them, null on a sale to an SAP business partner |
| GET | `/api/DesktopIntegration/sales/analysis` | Admin, Manager, Cashier, ApiUser. A period's takings, one section per currency, broken down by payment method (Cash, Swipe and Ecocash always listed; legacy spellings folded; `Not recorded` for none), day, hour in CAT, shop, business partner (`byBusinessPartner`, keyed by CardCode and labelled by the name the partner's sales carried, or the code where none did), source, operator and the 25 best-selling items, with each currency's sales count and takings over the same number of days just before (`previousFromDate`/`previousToDate`) under the same filters (`fromDate`, `toDate` — business dates, inclusive, default today, at most 366 days; `warehouseCode`, scoped exactly like the list; `sourceSystem`; `paymentMethod` — one tender by its reporting name, `Not recorded` for none) |
| GET | `/api/DesktopIntegration/sales/management-report` | Admin, Manager, Cashier. The management sales report: the period against the same number of days just before it (`previousFromDate`/`previousToDate`), one section per currency, by day, channel, business partner (`byPartner`, keyed by CardCode — one warehouse can serve several partners), shop or depot (`byDepot`, keyed by warehouse), cost centre, operator, payment method and vendor, with vendors who stopped buying, every item (average price, quantity and price change, units per sale, discount), SAP item groups, and item × partner and item × warehouse matrices; `partners`, every business partner that sold under the caller's scope in either period (the filter's list, unaffected by the channel and partner filters); posting and fiscalisation health across the period; and gross margin from SAP's booked gross profit on the sales' own invoices (`margin.available` false when SAP could not be read). Same parameters and scope as `sales/analysis`, plus optional `cardCode` to narrow to one business partner, at most 186 days |
| GET | `/api/DesktopIntegration/sales/management-report/item` | Admin, Manager, Cashier. One item from the management report taken apart by business partner, shop or depot, vendor, channel, operator and day, with price, discount and SAP margin measured on that item's lines only (`itemCode` required; `fromDate`, `toDate`, `warehouseCode`, `sourceSystem`, `cardCode` as for the report) |
| GET | `/api/DesktopIntegration/sales/review` | Admin, Manager, Cashier. The business review: `sales/analysis` and `sales/management-report` for the same period read together, one section per currency, with each shop's hours, days, per-trading-day takings, first sale and short tender, the 25 best sellers with cumulative share and ABC class, items priced differently between shops of the same channel, trade-size counter sales, and `findings` — written from those figures and ordered `action`, `review`, `note`. `fromDate`, `toDate`, `warehouseCode` as for the management report, at most 186 days |
| GET | `/api/DesktopIntegration/sales/review/pdf` | Admin, Manager, Cashier. The same review as an A4 PDF (same parameters) |
| GET | `/api/DesktopIntegration/sales/review/schedule` | Admin. When the review is emailed (weekly, Mondays 07:00 CAT; monthly, the 1st 07:00), to whom, which admin it is read as, and the last period each was sent for |
| PUT | `/api/DesktopIntegration/sales/review/schedule` | Admin. Save the schedule (`weeklyEnabled`, `monthlyEnabled`, `recipients`); the saving admin becomes the account the scheduled review is read as. Takes effect on the next 07:00 run |
| POST | `/api/DesktopIntegration/sales/review/email` | Admin, Manager. Email the review now under the sender's scope: `cadence` `weekly` or `monthly` for the last complete period, or `custom` with `fromDate`/`toDate`; `recipients`, or the schedule's list when empty. One message per recipient, PDF attached |
| POST | `/api/DesktopIntegration/sales/{externalReference}/post` | Post one held sale to SAP now: a till or offline van sale as an invoice of its own, or an online van sale's receipt row — signed, refused by SAP — through its reservation, with fiscalisation off. `outcome` is `Posted` or `AlreadyInSap`; a refusal is a problem naming the sale and what is wrong with it |
| POST | `/api/DesktopIntegration/sales/{externalReference}/fiscalise` | Retry a failed sale's fiscalisation now (asks the device for an existing receipt first) |
| POST | `/api/DesktopIntegration/sales/post-batch` | Post a named set of held sales, one invoice each (`externalReferenceIds`, at most 50) |
| POST | `/api/DesktopIntegration/end-of-day/consolidate` | Consolidate the day's sales |
| GET | `/api/DesktopIntegration/end-of-day/report` | The day's report (`reportDate`) |
| POST | `/api/DesktopIntegration/end-of-day/email-report` | Email it (`reportDate`) |
| GET | `/api/DesktopIntegration/vendors` | The vendors this account may invoice, for a cart-vendor till |

`POST .../sales` deduplicates on `externalReferenceId`, and **the status line says which answer you
got**: `201 Created` for a sale this request made, `200 OK` for one that already existed under that
reference. Resending an unconfirmed sale under the same reference is the supported way to retry, and
is what stops one basket becoming two invoices — but the reference is spent once a sale exists under
it. Sending a *different* basket under a reference that already has a sale is refused with
`409 Idempotency.RequestMismatch` rather than answered with the earlier sale.

Both were 201 until 10 September 2026, and the mismatch was only refused inside the idempotency
window, so a reference reused the next day was answered with the previous day's invoice under a 201
that a client could not tell from a creation. Sales created before that date carry no request
fingerprint and are still replayed without the comparison; the server logs when it does so.

**Posting is not consolidating.** Consolidating is the end-of-day run that folds a day's
legacy-desktop sales into one invoice per customer. The two posting routes send till, vending and van
sales to SAP as one A/R invoice each — the same thing the background pass does every minute, asked
for now. They exist because the pass gives up after a few attempts, and a sale it has parked is
invisible to every later pass; a person pressing Post has usually just fixed whatever SAP was
refusing, so the attempt cap is deliberately not applied. Nothing else is relaxed.

Both are **safe to send twice**. Each sale is claimed in `IDesktopSalePostGuard` — a durable,
cross-instance claim keyed on the sale's own external reference — before anything reaches SAP, so a
repeat is answered with the invoice the first attempt created rather than raising a second, and a
person pressing Post while the background pass is mid-post is refused rather than allowed to run
alongside it. That matters more here than the wording suggests: the customer already holds a ZIMRA
receipt, so a duplicate SAP invoice can only be undone by a manual credit note.

The batch answers **200 with a row per sale**, not a single status: each sale becomes its own SAP
document, there is nothing to roll back, and an all-or-nothing status could only misdescribe what
happened. Each row carries an `outcome` of `Posted`, `AlreadyInSap`, `InProgress` (another post holds
the claim), `Failed` (SAP refused it) or `NotPostable`, with `sapDocEntry`/`sapDocNum` where one
exists. A batch that outlives the caller's timeout keeps posting; re-sending it is the intended
remedy. An account that may not post the sales at all is refused the whole request rather than
reported row by row, because that is a statement about the caller and would be true of every row.

Which sales may be posted is decided by `DesktopSalePostEligibility`, and the sales list reports it
per row as `postRefusal` (null when the sale may be posted) so a client offers a button exactly where
the command would accept one. Both routes are additionally scoped to the caller's own shop, so a
shop-scoped account cannot post another shop's takings.

**A sale can be held rather than refused.** When a post leaves for SAP and no clear answer comes
back — a timeout, a dropped connection — SAP may hold the invoice without showing it yet, so the
sale is not sent again for `DesktopSalePosting:UnresolvedPostGraceMinutes` (van sales:
`VanSalesPosting:UnresolvedPostGraceMinutes`, both default 15). The sales list reports that as
`postHeldUntilUtc`: when the sale may be sent again, or null when it is not held. `lastPostingError`
keeps what the post actually failed with through the hold. A post requested inside the window is
answered `Failed` with a message naming the time the hold ends, and nothing is sent. The scheduled
pass leaves a held sale out of its batch until the hold ends, so SAP is asked about it once per hold
rather than once a minute; a person's request asks SAP straight away. A failure
before the invoice leaves — the SAP login, the invoice series lookup, an open circuit — holds
nothing, and the sale is retried on the next pass. The same holds for the desktop credit memo and
the daily incoming payment: a failed login before either is sent leaves no post marker, so the
payment is not held as unresolved and is retried on the next run.

**A credit is two documents, and ZIMRA comes first.** `POST .../credit-notes` files the fiscal credit
against the original REVMax receipt and answers as soon as that is settled; the SAP credit memo
follows on its own. That ordering is the opposite of every other document in this API, and it is
inherited from the sale: a till sale is fiscalised the moment it is rung up and posts to SAP hours
later, so between those two moments the receipt in the customer's hand is the only document that
certainly exists — and that is exactly the window in which somebody walks back in with the goods.

Each saved credit therefore reports both halves, and neither is inferred from the other. `status` is
the fiscal one — `Prepared`, `Submitting`, `Fiscalised`, `Rejected` or `ReconciliationRequired`.
`sapStatus` is the back-office one:

| `sapStatus` | Meaning |
|--------------|---------|
| `Deferred` | The sale has not posted yet. The memo is raised automatically the moment it does. |
| `Posted` | SAP holds the credit memo; `sapDocNum` names it. |
| `Failed` | SAP refused it, or could not be asked. Retried by the sweep; `sapError` says why. A post that went out without a clear answer is not sent again until `DesktopSalePosting:UnresolvedPostGraceMinutes` (default 15) has passed, unless SAP shows the memo first. |
| `NotRequired` | The sale was excluded from posting, so SAP is owed nothing. |
| `ManualInSap` | The sale reached SAP inside a consolidated invoice, or its credited lines cannot be tied to the invoice's own lines. A person raises it, then records it with `mark-raised`. Until then the returned units stay on the stock ledger. |
| `FiscalOnly` | Created with `postToSap: false` against a sale already in SAP. ZIMRA holds the credit; no memo is ever raised and no units go back on the stock ledger. For fiscal corrections, such as a sale filed with ZIMRA twice. |

The request's `postToSap` is optional. `false` on a sale that has not posted still owes SAP the memo
(`Deferred`). `false` on a sale that has posted is `FiscalOnly`, and requires `saleInSap: true` — what
the form showed — so a form read before the sale posted is refused rather than taken to mean "SAP never
hears of this". `true` on a sale that has not posted is refused.

**A receipt is never credited past its total, counting credits filed elsewhere.** `prepare` answers
`remainingAmount` — the receipt total less credits saved here and less `source.externalCreditedAmount`,
the credit receipts found on the device under the customer's SAP credit memo numbers that reference
this receipt (listed in `source.externalCredits`). Creating a credit worth more is refused. For a sale
in SAP, a SAP read or device lookup that cannot answer refuses both calls rather than reporting no
credits; a sale with no SAP invoice is not checked.

**Nothing is raised in SAP against a credit that is not `Fiscalised`.** A credit the device refused,
or one whose outcome nobody has established, never becomes a SAP document — which is why
`ReconciliationRequired` is worth resolving rather than leaving.

The memo is always based on the invoice (`BaseType`/`BaseEntry`/`BaseLine`), never standalone, so SAP
takes the batches from the document being credited; a batch-managed line with no batch selection is
refused and the whole document with it. It carries the credit's own `DCN-` number in `NumAtCard`,
which is what makes a retry find the memo a lost reply left behind rather than raise a second.
The credited units are returned to the shared stock ledger as soon as ZIMRA accepts the credit —
hours before SAP may see it — because those units never left the ledger through SAP in the first
place.

The vendor route takes **no business partner and accepts none**. It reads the code off the
signed-in account through `SellingAccountResolver` — the same value `POST .../sales` resolves
`vendorCode` against — so the list an operator picks from and the set the server will accept are one
filter over one value, and a till cannot reach another shop's vendors because it never names one.
Use `/api/route-customers` for the administrative view, which filters on a code the caller supplies
— and, because a vendor is not a route customer, needs `scope=vending` to list them at all.

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

#### Tax

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/DesktopIntegration/tax/item-rates` | Every item's SAP VAT group and the rate charged for it, plus the rate an item this does not name falls to |

The till prices its own basket — it adds VAT on screen and prints VAT on a receipt before a sale has
been posted, and it does that offline — so it needs the answer `CreateDesktopSaleHandler` will reach.
This serves it from `SapItemTaxGroups`, the same table that stamps a sale line's tax code, mapped
through `Tax:RatesByTaxCode`: one source, so the basket and the invoice cannot disagree about an item.

The whole catalogue, not one warehouse's — the item master is not warehouse-scoped, and a receipt
reprinted for an item the shop no longer carries still has to state the VAT charged that day. An
empty `items` list means `SapItemTaxGroupWarmJob` has not completed a pass; it is answered rather than
refused, and a client should keep whatever rates it already holds rather than fall back to the
standard rate for everything.

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

**There is no fiscal proxy in this API.** Nothing under `/api/revmax/*` is exposed — those routes were
removed on 2026-08-10 and were not restored when REVMax itself was. Device status, licences, Z-reports,
fiscal-day open/close and the receipt archive are functions of the provider's own console or device,
not of this API.

**There are two providers, and only one is live.** `Fiscalisation:Provider` selects between them:

| Provider | What it is | State |
|----------|------------|-------|
| `Revmax` (default) | The vendor device on the LAN at `Revmax:BaseUrl` | **Live.** Everything is filed here |
| `Platform` | The in-house ZIMRA FDMS platform at <https://fiscal.kefaloscheese.com/> | Registered, wired, dormant |

REVMax was decommissioned from this codebase on 2026-08-10 and restored on 2026-09-09. That reversal is
not a verdict on the platform: ZIMRA has not issued it a production device, so it has nothing to file
against. `Fiscalisation:Provider=Platform` is the whole switch once that device exists. An unset or
unparseable value lands on REVMax deliberately.

What follows describes the **platform**: how this API calls it, its `X-API-Key` and the settings
endpoints that manage it have no effect while the provider is REVMax. The fiscal fields on an invoice
and the fiscalise endpoint at the end are provider-agnostic and are marked where they differ. For the
live path see [32a. REVMax](#32a-revmax).

**How this API uses the platform**

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
`fiscalReceiptGlobalNo`, `fiscalVerificationCode`, `fiscalDeviceId`, `fiscalDay`, `fiscalizedAtUtc`.
These come from the local projection in `DesktopFiscalTransactions`, not from a live call. The invoice
PDF prints the verification code, fiscal day and device id beside the QR; when the projection has no QR,
the PDF download asks the fiscal device and fills all of them from its answer.

**Who composes the QR code differs by provider.** Under the platform it is composed by this API, which
returns neither the payload nor the verification code: the verification segment is the first 16 hex
characters of `MD5(deviceSignatureValue)`, appended to the device's `qrUrl` along with the device id,
receipt date and receipt global number. See `FiscalReceiptQrComposer`. Under REVMax the **device**
composes both and returns them on the transaction response, so the composer is not used.

**Triggering fiscalisation**

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/Invoice/{docEntry}/fiscalize` | Fiscalise a posted invoice |

Fiscal status is also reconciled in the background by `InvoiceFiscalStatusBackfillService`; there is
no longer an on-demand backfill endpoint.

---

### 32a. REVMax

**The live fiscal path.** A vendor device on the LAN at `Revmax:BaseUrl` (`http://172.16.16.201:8001`),
reached through `IRevmaxClient` and driven by `RevmaxFiscalizationService`. **No route on it carries any
authentication**, which is one reason it is not proxied: exposing it through this API would put an
unauthenticated fiscal device behind an authenticated one.

Callers never touch it directly. Writes go through `IFiscalizationService` and read-back through
`IFiscalReceiptReader`; both resolve by provider, so an invoice handler is identical under either.

**Device operations this API calls.** These are the **device's** routes, not this service's — they are
served by the REVMax box, and every one of them sits under `http://172.16.16.201:8001/api/RevmaxAPI/`.
Nothing in the table below is reachable on this API.

| Purpose | Method | Operation |
|---------|--------|-----------|
| File an invoice, or a credit note against a receipt **this** device filed | POST | `TransactM` |
| File a credit note whose original was filed on **another** device | POST | `TransactMExt` |
| Ask whether a document is already fiscalised, and read its receipt back | GET | `GetInvoice/{invoiceNumber}` |
| Device identity, licence and fiscal-day status | GET | `GetCardDetails`, `GetLicense`, `GetDayStatus` |

`ZReport` **closes the fiscal day** and is never called from this API — a separate Windows service owns
the daily close. Do not call it to read anything.

**Picking the endpoint is the routing decision that matters.** The request type is shared and both
endpoints accept the `refDeviceId` / `refReceiptGlobalNo` / `refFiscalDayNo` back-reference, so only the
endpoint distinguishes the two cases and choosing wrong is silent — it files the credit note as though it
reversed some other device's receipt. Route on the original receipt's `DeviceID` against
`Revmax:DefaultRefDeviceId`.

**Every refusal is `HTTP 200` carrying `Code: "0"`.** There is no 4xx on this device, and a duplicate is
refused only by the wording of the message. Treating the status code as the outcome reads every refusal
as a success.

**The device is the authority on whether a document is fiscalised — our log is not.** The device vendor's
own SAP B1 add-on files invoices to this same box and writes nothing to our database, so a document ZIMRA
already holds a receipt for can read as un-fiscalised here. `GetInvoice` is the only thing that can
answer the question. It is **not scoped to our device**: the box serves several, every one of them reports
the same `DeviceSerialNumber`, and invoices and credit notes share one number namespace with our SAP
DocNums. Check `DeviceID` **and** `receiptType` before adopting any receipt as ours.

**Reads, writes and what may be retried**

A `TransactM` POST is attempted **once**. An indeterminate outcome — a timeout above all — is resolved by
asking `GetInvoice`, never by resubmitting: the receipt may already exist, and a duplicate fiscal receipt
can only be undone with a manual credit note. Only reads are retried.

**Tax ids are REVMax's own, not FDMS's**

`Revmax:TaxIdMappings` maps a SAP VAT group (`OVTG.Code`) to the id declared on the line, and those are
**not** the FDMS ids in `Fiscalisation:TaxIdMappings` — REVMax sits in front of FDMS and maps its own on
the way through. Do not copy one section into the other. The rate that accompanies an id comes from
`Tax:RatesByTaxCode`, so the rate charged on the invoice and the rate declared on the receipt cannot
drift apart, and `VerifyDeclaredTaxAsync` reads the filed receipt back and raises
`TaxDeclarationMismatch` if they did. That never flips `Success` and never asks for a retry — the receipt
exists, and resubmitting would only add a second.

**Van sales are fiscalised server-side under this provider.** The device is on the LAN, not in the van,
so no handset can sign for it: offline sales arrive unstamped and `DesktopSaleFiscalisationSweep`
fiscalises them after the fact. `VanSalesSignedReceiptIngestService` no-ops, and
`RefusesUnstampedVanSales` is false — enforcing a signature requirement here would refuse every van sale
in the fleet for want of a signature that cannot exist.

**Fields the device ignores.** `AMT` and `InvoiceAmount` are recomputed — each line as `QTY × PRICE` and
the document as the sum of those — so `PRICE` is the only lever and cent-level drift against SAP is
normal. `InvoiceComment` must be non-empty, and the customer fields must be present even when blank, or
the device dereferences a null and answers with what looks like a fault on its side.

**Verifying a change**

`scripts/RevmaxProbe` drives the real service against the live device read-only and prints the exact
payload it would send without sending it. `scripts/FiscaliseInvoice` dry-runs unless given `--post`.
Unit-level equivalents are in `ShopInventory.Tests/RevmaxFiscalPayloadTests.cs`. Dry-run every new
document shape before posting: a filed receipt cannot be withdrawn.

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
| `/hubs/notifications` | The SignalR hub, on this service's own address — for callers inside the network |
| `/api/hubs/notifications` | The same hub, for callers arriving through the reverse proxy |

Both paths serve the one hub, and which to use is settled by where the caller sits rather than by
what it wants. The proxy routes only `/api` and `/swagger` here, so anything outside the network —
a till, for one — must use the `/api` path; the web app connects container-to-container and uses
the short one.

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
**Audit:** every **read** — the six reports, `routes`, `route-stops` and both `visits` endpoints — is
written to the audit log by `VanSalesPortalReadAuditFilter`, **query string included**, so the row says
whose figures were pulled, on which route, for which period. The writes already log from their
handlers and are not recorded twice.

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/van-sales/compliance-report` | `vansales.attendance.view` | Departure compliance: a row per rep per trading day |
| GET | `/api/van-sales/performance-report` | `vansales.attendance.view` | What sold, by territory and route, by rep, by item, over time |
| GET | `/api/van-sales/sales-analysis` | `vansales.attendance.view` | The sales breakdown for the vans: takings by tender, day, hour, van, customer, channel, rep and item |
| GET | `/api/van-sales/coverage-report` | `vansales.attendance.view` | Who the vans are reaching and who they are losing |
| GET | `/api/van-sales/replenishment-report` | `vansales.attendance.view` | How well the depots are keeping the vans stocked |
| GET | `/api/van-sales/stock-report` | `vansales.attendance.view` | What each van carried, sold, and is still riding around with |
| GET | `/api/van-sales/routes` | any of `vansales.attendance.view`, `users.view`, `users.create_merchandiser_accounts` | The selling routes |
| POST | `/api/van-sales/routes` | `users.edit` or `vansales.routes.manage` | Create a route |
| PUT | `/api/van-sales/routes/{id}` | `users.edit` or `vansales.routes.manage` | Update a route |
| GET | `/api/van-sales/route-stops` | any of `vansales.attendance.view`, `users.view`, `users.create_merchandiser_accounts` | The areas each route works, and when |
| POST | `/api/van-sales/route-stops` | `users.edit` or `vansales.routes.manage` | Add an area to a route's plan |
| PUT | `/api/van-sales/route-stops/{id}` | `users.edit` or `vansales.routes.manage` | Edit an area on a route's plan |
| DELETE | `/api/van-sales/route-stops/{id}` | `users.edit` or `vansales.routes.manage` | Drop an area from a route's plan |
| POST | `/api/van-sales/route-stops/reorder` | `users.edit` or `vansales.routes.manage` | Put one weekday's or cycle week's stops in order |
| GET | `/api/van-sales/telematics/vehicles` | `users.edit` or `vansales.routes.manage` | The fleet telematics vehicles, for assigning one to a route |
| GET | `/api/van-sales/fleet-audit` | `vansales.attendance.view` | The fleet over a period: a row per vehicle, each carrying its own days |
| PUT | `/api/van-sales/fleet/{registration}/business-partner` | `users.edit` or `vansales.routes.manage` | Link a vehicle to the van sales account whose takings it carries |
| GET | `/api/van-sales/visits` | `vansales.attendance.view` | A page of van sales calls, newest first |
| GET | `/api/van-sales/visits/report` | `vansales.attendance.view` | Time on the round, summarised per rep |
| GET | `/api/van-sales/invoices` | `invoices.view` | Invoices the van sales app created, with their ZIMRA and SAP state, newest first |
| GET | `/api/van-sales/invoices/{reference}` | `invoices.view` | One of those invoices by van order: its lines, receipt and posting history, and `postRefusal` / `fiscaliseRefusal` — why `POST /api/DesktopIntegration/sales/{reference}/post` and `.../fiscalise` would refuse it, null where they would not; the same rules Desktop Sales offers its buttons on |
| GET | `/api/van-sales/credit-notes` | `invoices.view` | Credit notes raised against van sales invoices, with the invoice each reverses |

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

Each day also carries `telematics` — what the vehicle did, beside what the rep recorded. It is
**null if and only if fleet telematics is switched off for the whole report**; when the feature is
on, every day carries one, including days whose truck could not be looked up. That makes "the
feature is off" and "this van has no data" structurally different rather than a convention:

| `match` | Meaning |
|---|---|
| `NoRegistration` | Neither the day nor the rep's route names a truck |
| `NotInFleet` | A truck is named but the provider does not hold it — a typo, or a vehicle sold |
| `Matched` | Looked up. Whether it reported is `hasRollup`, and whether the read succeeded is `movementRead` |

`firstIgnitionOn` and `firstDeparture` are **different questions** and both are given.
`verifiedDeparture` is the second falling back to the first, and `effectiveDeparture` is that
falling back to the rep's own `timeOut` — which is the time a late departure is judged against.
The gap between ignition and departure is real: measured across five days on one truck it ran from
eighteen minutes to five and a half hours, because a driver warming a diesel or pulling a fridge
down to temperature turns the key long before the van goes anywhere.

`distanceKm` is the provider's own figure, never the difference of the two odometer readings —
a vehicle can report a distance of zero with an end reading below its start. Treat it as
meaningless when `odometerReset` or `terminalChanged` is true.

The result carries a `telematics` status of its own — `enabled`, `configured`, `ready`,
`lastSyncedAt`, `coveredFrom`, `coveredThrough` and `reason`. **`reason` is a sentence to print
verbatim** and is null when everything is in order: an absent vehicle column has four different
causes and each needs a different thing done about it, so the caller is told which rather than
left to compose an explanation. When `ready` is false the per-day `telematics` records are still
present but carry no figures, because a projection that has only backfilled to September must not
answer for July — rendering July as "the van never moved" reads as a finding.

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
      "rtiOutstanding": 2,
      "telematics": {
        "registration": "AEK4471",
        "match": "Matched",
        "hasRollup": true,
        "movementRead": true,
        "odometerRead": true,
        "firstIgnitionOn": "2026-08-15T06:12:00",
        "firstDeparture": "2026-08-15T07:05:00",
        "lastIgnitionOff": "2026-08-15T17:08:00",
        "departureLatitude": -17.79,
        "departureLongitude": 31.03,
        "ignitionCycleCount": 11,
        "drivingMinutes": 264,
        "idleMinutes": 91,
        "distanceKm": 151,
        "odometerReset": false,
        "terminalChanged": false,
        "vehicleStateLabel": null,
        "verifiedDeparture": "2026-08-15T07:05:00",
        "isVerified": true,
        "didNotMove": false
      },
      "effectiveDeparture": "2026-08-15T07:05:00",
      "departureIsVerified": true,
      "departureDiscrepancyMinutes": 25,
      "odometerDivergenceKm": 4
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

##### GET `/api/van-sales/sales-analysis`

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 29 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `warehouseCode` | — | One van's warehouse; every van when omitted |
| `paymentMethod` | — | One tender by its reporting name; `Not recorded` for sales that named none |

**Response:** `DesktopSalesAnalysisResult`, the desktop analysis's shape, so `/van-sales/reports/sales-breakdown` is the desktop breakdown page. The breakdowns mean the van's own thing: `byWarehouse` is the van, `byBusinessPartner` the route customer (never the document card, which is the van's own account), `bySource` `online`/`offline`, and `byOperator` the rep. Reads both tables through `VanSalesFactReader`, so online sales are counted — the desktop analysis cannot see them. VAT is known on offline receipts only; an online sale's is on its SAP invoice.

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

**Response:** `List<RouteDto>` — `id`, `code`, `name`, `territory`, `truckRegNo`,
`temperatureMinC`, `temperatureMaxC`, `temperatureProbeChannel`, `isActive`, `assignedUserCount`.

The three temperature fields are the round's cold chain. Both limits null — the ordinary case —
means the route carries nothing chilled and is never judged on temperature.
`temperatureProbeChannel` says which of the tracker's four probes reads the load box, or null for
the lowest-numbered probe that reports; the fleet API publishes no capability flag for
temperature, so it can only be told, never discovered.

##### POST `/api/van-sales/routes` · PUT `/api/van-sales/routes/{id}`

**Body:** `SaveRouteRequest`. There is no delete — a route names historical days.

```json
{
  "code": "HN02",
  "name": "Harare North 2",
  "territory": "Harare North",
  "truckRegNo": "AEK 4471",
  "isActive": true,
  "temperatureMinC": -18.0,
  "temperatureMaxC": -12.0,
  "temperatureProbeChannel": 1
}
```

**Response:** `RouteDto`. `409 Conflict` on a duplicate code; `PUT` also answers `404` for an
unknown id. `400` when one temperature limit is set without the other, when the lower limit is
above the upper, or when the probe channel is outside 1-4.

##### GET `/api/van-sales/telematics/vehicles`

The vehicles the fleet telematics provider knows about, for choosing one on the route editor
rather than typing a registration. It carries no positions and no movements — only which
vehicles exist and what each tracker can measure — which is why it is gated like the route
writes rather than like the reports.

| Parameter | Default | Notes |
|-----------|---------|-------|
| `includeRetired` | `false` | Bring back vehicles that have left the fleet. A route still naming one needs this, or the picker cannot show what it is set to |

**Response:** `TelematicsVehiclesResult` — `enabled`, `configured`, `lastSyncedAt`, `reason`,
`vehicles`. Each vehicle carries `registration`, `registrationNormalized`, `clientVehicleName`,
`description`, `hasAnyFuelSensor`, `hasTemperatureProbe`, `isActiveInFleet` and `stateLabel`.

`reason` is a sentence to print verbatim, and is null when there is nothing to explain. An empty
`vehicles` list has four different causes — telematics switched off, no credentials, the sync has
not run yet, or an account that genuinely holds no vehicles — and each needs something different
done about it, so the caller is told which rather than left to guess.

`registrationNormalized` is the plate reduced to letters and digits, upper case: it is what the
compliance report joins on, because the same truck is spelled several ways across this system and
the provider's console. `clientVehicleName` is the fleet's own name for the vehicle and is **not**
a registration — this account names one "306_AFQ9644" — so it must never be matched as one.

`hasTemperatureProbe` is null until a sync has looked, and is inferred from readings actually
arriving rather than declared: the provider publishes capability flags for fuel and electric and
nothing at all for the four temperature channels.

##### GET `/api/van-sales/fleet-audit`

The same period the compliance report covers, read from the other side: what did each **truck**
do. A fleet manager is looking for the vehicle that is barely moving or barely reporting, and a
rep-day grid buries both.

| Parameter | Default | Notes |
|-----------|---------|-------|
| `fromDate` | today − 30 days | Inclusive CAT trading day |
| `toDate` | today | Inclusive CAT trading day |
| `registration` | — | One vehicle, in any spelling; the whole fleet when omitted |

**Response:** `FleetAuditResult` — `fromDate`, `toDate`, `vehicles`, and the same `telematics`
status block the compliance report carries, whose `reason` is printed verbatim.

Each vehicle gives what it is (`registration`, `vehicleName`, `description`, `stateLabel`), what
it can measure (`hasAnyFuelSensor`, `hasTemperatureProbe`), the account it carries
(`businessPartnerCode`, `businessPartnerName`), period totals, and a `days` array holding **every
trading day in the period** — not only the days it reported.

That last point is the shape worth knowing. A truck that said nothing for a fortnight returns a
fortnight of days with `hasRollup: false`, because an empty array reads as a short period rather
than as a dead tracker. `daysWithData` and `daysSilent` are the pair to read together, and
`neverReported` is true when a vehicle reported on no day at all — a tracker to look at rather
than a van to ask about.

Each day carries `movementRead` and `odometerRead` separately from the figures, because a null
distance on a day nobody could read is a different finding from a null distance on a day the van
sat still. `didNotMove` distinguishes the second.

**Fuel and cold chain.** Each day carries a `fuel` and a `temperature` object, either of which is
null when that read has not succeeded. A null `fuel` on a vehicle whose `hasAnyFuelSensor` is false
means no sensor is fitted; on one where it is true it means the fuel read failed. Those are
different findings, and the page says which.

`fuel` gives the tank level at each end of the day, `usedLitres` (the provider's estimate, which
accounts for fills — the difference of the two levels does not), `consumedLitres` (engine-reported,
CAN vehicles only), `isCalibrated`, `readingsSettled`, `fillCount` and `filledLitres`. `trustworthy`
is true only when the sender is calibrated **and** both levels and every fill are settled; recent
readings never are, so today's figure is provisional by construction. Fills the provider lists
twice are counted once.

`temperature` gives the judged probe `channel`, `sampleCount`, `minC`/`maxC`/`avgC`, the first and
last reading, and the `limitMinC`/`limitMaxC` the day was judged against. Those limits are copied
from the route when the day is first judged and **never loosened afterwards**, so widening a
route's range cannot erase an earlier breach. `minutesAboveMax`/`minutesBelowMin` are null on a
round with no limits and zero on one that stayed in range; `breached` is true when limits were set
and the probe spent time outside them. A reading is taken to hold until the next one, capped at
`Cartrack:TemperatureSampleGapCapMinutes` (default 30), so a reporting gap is never counted as time
spent warm. `sampleCount: 0` on a judged round is the cold-chain blind spot — nobody can say the
load stayed cold — and is not collapsed into null.

The vehicle totals add `daysWithFuel`, `fuelUsedLitres` (null when no day gave a figure),
`fuelAllTrustworthy`, `fuelFillCount`, `fuelFilledLitres`, `daysWithTemperature` (days asked),
`daysWithReadings` (days the probe answered), `daysBreached`, `minutesOutsideLimits` (null when no
day was judged) and the period's `temperatureMinC`/`temperatureMaxC`.

Every reading is also kept in `VehicleTemperatureSamples` as cold-chain evidence, because the
provider retains temperature for about two months. A weekly job removes readings older than
`Cartrack:TemperatureSampleRetentionDays` (default 730, never less than 365).

**Money is only present where it can be attributed.** `sales`, `saleCount`, `totalSales`,
`salesPerKm` and `kmPerSale` are null on a vehicle with no `businessPartnerCode` — a van nobody
has linked has not sold nothing, it has simply not been mapped. Takings come through the shared
van sales fact reader, so an online sale that posted straight to SAP and left only a confirmed
stock reservation is counted alongside the offline ones.

##### PUT `/api/van-sales/fleet/{registration}/business-partner`

Links a vehicle to the van sales account whose takings and stock it carries. This is what lets a
truck's kilometres be read beside the money its van moved.

**Body:** `LinkVehicleBusinessPartnerRequest`. An empty or omitted code unlinks.

```json
{
  "businessPartnerCode": "VAN010",
  "businessPartnerName": "Van Sales CBD"
}
```

The name is a label carried so the fleet list reads without a second lookup; everything joins on
the code.

**Response:** `TelematicsVehicleDto`. `404` when the registration is not in the telematics fleet.
`400` when the code is not one of the van sales accounts — linking a truck to an ordinary
customer would report that customer's entire trade as this van's takings, which is a wrong figure
that looks entirely plausible. `409` when another vehicle already holds that account, because two
trucks sharing one would each report its full takings and the same money would be counted twice.

##### GET `/api/van-sales/route-stops`

The published schedule: which areas a route works, and when.

| Parameter | Default | Notes |
|-----------|---------|-------|
| `routeId` | — | One route; every route when omitted |
| `includeInactive` | `false` | Bring back stops dropped from the plan too |

**Response:** `List<RouteStopDto>` — `id`, `routeId`, `routeCode`, `routeName`, `name`, `dayOfWeek`,
`weekNumber`, `alternateSet`, `sequence`, `isActive`.

`dayOfWeek` and `weekNumber` are **both nullable, and null means something different in each**. A
town truck works a weekday every week, so it has a `dayOfWeek` and no `weekNumber`; an upcountry
route runs a repeating cycle and is away for days at a time, so it has a `weekNumber` and no
`dayOfWeek` — the schedule commits to the week, not to which morning the van reaches a given town.
Null in `weekNumber` is therefore not week 1, and a client that defaults it to 1 makes every weekly
round look like the first week of a fortnightly one. `dayOfWeek` uses .NET's own numbering,
**Sunday = 0**.

`alternateSet` is 0 for the standard plan and 1 or above for a published alternative to it — West 2's
Wednesday is Dzivarasekwa and Whitehouse *or* Hatcliff and Mungate. Both sets are the plan, and a
client that merges them doubles the day's planned coverage.

##### POST `/api/van-sales/route-stops` · PUT `/api/van-sales/route-stops/{id}`

**Body:** `SaveRouteStopRequest`.

```json
{
  "routeId": 4,
  "name": "Dzivarasekwa",
  "dayOfWeek": 3,
  "weekNumber": null,
  "alternateSet": 0,
  "sequence": null,
  "isActive": true
}
```

`sequence` null appends to the stop's own day or set rather than putting it first. `409 Conflict` for
the same area twice in one route, day, week and set — the refusal names the stop as the schedule
spells it. Posting the name of a stop that has been **dropped** revives that row instead of refusing
or creating a second one. `PUT` answers `404` for an unknown id, and either verb answers `404` when
no such route exists.

##### DELETE `/api/van-sales/route-stops/{id}`

Drops an area from the plan. A deactivation, not a delete: the row is kept so that "no longer called
on" and "never called on" stay different histories. Answers `204`, and `204` again on a repeat — the
caller asked for a state, not a transition.

**Every edit here outlives a deploy.** The published schedule is loaded at start-up from
`VanSalesRouteSeedData`, and each seeded row records what the seeder *placed* on it rather than what
it currently says, so a stop that has been renamed, moved to another day or dropped — and a route
whose code has been corrected — is still recognised as already-seeded and is left alone. Only a stop
added to the seed list later, which nothing yet carries, arrives on the next start.

##### POST `/api/van-sales/route-stops/reorder`

Puts one heading's stops into the order the van works them, and renumbers `sequence` from 1.

**Body:** `ReorderRouteStopsRequest`. The heading is named the way a stop names it — `dayOfWeek`,
`weekNumber` and `alternateSet`, with the same meanings for null.

```json
{
  "routeId": 4,
  "dayOfWeek": 1,
  "weekNumber": null,
  "alternateSet": 0,
  "stopIds": [17, 15, 16]
}
```

`stopIds` must be **exactly** the active stops that heading holds. An order that omits one, or that
names a stop from elsewhere, is `409 Conflict` rather than partially applied: both come from a page
that has gone stale, and applying half of one leaves the omitted stop holding an old number in the
middle of the new sequence — an order nobody chose. Dropped stops are not part of the heading and are
not named. `404` for an unknown route, or a heading with no stops left.

**Response:** `List<RouteStopDto>` in the new order.

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
| POST | `/api/vansales/sales-order` | `salesorders.create` | Create a sales order. Posted to SAP by the post-save queue once priced, without waiting for approval on the web; an order over its credit limit stays Pending for web approval |
| POST | `/api/vansales/sales-order/history` | `salesorders.view` | Search — a POST because the filter is a body |
| POST | `/api/vansales/order/history` | `invoices.view` | Invoice history; also a POST |
| GET | `/api/vansales/fiscal` | `invoices.view` | Fiscal device details for the handset |
| GET | `/api/vansales/fiscal/lease` | `invoices.create` | Optional `pendingSales`. Returned **bare**, not enveloped |
| POST | `/api/vansales/fiscal/day-close` | `invoices.create` | The close a handset signed for its own fiscal day. Held rather than forwarded — the day is packaged once its receipts have landed |
| POST | `/api/vansales/pod` | `invoices.view` | Upload proof of delivery |
| POST | `/api/vansales/pod/{order}/file` | `invoices.view` | One page of a delivery note, as `multipart/form-data` (`file`, `description`, `externalReference`, `isAdditionalPage`). The van sales mirror of `POST /api/invoice/{docEntry}/pod`, which is gated on a role list carrying `SalesRep` — a different role from the van's `Sales`. Preferred over `POST /api/vansales/pod` above, which carries whole photographs as base64 in a JSON body and sends a note's pages in one request, where the double-submit window reads all but the first as duplicates |
| POST | `/api/vansales/pod/invoice/{docEntry}/file` | `invoices.view` | The same page upload as `pod/{order}/file`, filed against a SAP invoice by its document entry. What the delivery list below hands out. `pod/{order}/file` tries its number as a platform order or invoice id first, so a document entry that happened to equal one would be filed against a different document |
| GET | `/api/vansales/pod/deliveries` | `invoices.view` | `fromDate`, `toDate` (`yyyy-MM-dd`, both inclusive, at most 31 days apart). Every invoice for the shops on the drivers' shop list — the one allowlist the portal's Driver Shop Access page writes onto every `Driver` and `PodOperator` account — with `hasPod`, `podCount`, who filed and whether it was credited in full. The same report as `GET /api/Invoice/pod-upload-status`, scoped to that list explicitly because a van rep is `Sales` or `ADR`, not `Driver`. `shopCount` 0 means no shops are on the list, which is not the same answer as no invoices |
| POST | `/api/vansales/order` | `invoices.create` | Direct invoice. `202` when queued rather than posted |
| POST | `/api/vansales/order/with-batches` | `invoices.create` | The same action as `/order` — one more route on it, not a second endpoint |
| POST | `/api/vansales/sales` | `invoices.create` | Take custody of offline, already-ZIMRA-stamped sales |
| POST | `/api/vansales/order/convert-to-invoice` | `invoices.create` | Always `202`. The end-of-day consolidated invoice is based on the order in SAP (`BaseType` 17) up to each line's open quantity; the rest, or an order SAP will not invoice against, goes on ordinary lines |
| POST | `/api/vansales/stock/position` | `inventory.transfer` | What the van is carrying, as its own handset counts it. Becomes that van's stock snapshot for the trading day — the first count of a day is the one kept |
| GET | `/api/vansales/stock/position` | `inventory.transfer` | What the van is carrying now: the morning count, plus loads transferred in since, less every sale received today. For a handset that has lost its own ledger. `counted: false` means the position is unknown, not that the van is empty |
| POST | `/api/vansales/inventory/request` | `inventory.transfer` | Ask the depot for stock. `201` |
| GET | `/api/vansales/inventory/request` | `inventory.transfer` | The caller's transfer requests |
| POST | `/api/vansales/inventory/confirm` | `inventory.transfer` | Confirm a transfer into the van |
| POST | `/api/vansales/breakages` | `vansales.breakages.report` | Report broken stock collected from a shop. `201`. Moves nothing in SAP — see [Market Breakages](#52-market-breakages) |
| GET | `/api/vansales/breakages` | `vansales.breakages.report` | The caller's own breakage reports from the last `days` days (default 30, at most 90), with status and the office's count |

`POST /api/vansales/sales` is not `POST /api/vansales/order` with a flag. Nothing on it reaches SAP
during the request — the batch is held for the end-of-day posting run — and nothing on it is
fiscalised, because the customer is already holding the printed receipt. Per-sale outcomes come back
individually so one bad row cannot strand a van's whole backlog on the handset.

A load that lands on the van *after* its morning position is not a route at all: the transfer
listener's webhook raises `StockTransferReceivedEvent`, and the van sales feature answers it with a
silent push to the reps assigned to that warehouse, on which the handset re-reads its warehouse
catalogue and reconciles its ledger. See [Push Notifications](#42-push-notifications).

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

**Vending vendors share this table and are not route customers.** A van route is a round the van
drives and its route customers are the shops on it; a vending vendor sells from a cart out of a depot
and is on no round at all — it has a depot, not a route. Nothing on a row says which it is: a vendor
is a row under a business partner a `CartVendor` account sells on, the same reading the vending
overview makes. The three read endpoints therefore take `scope`:

| `scope` | Answers with |
|---------|--------------|
| `route` (**default**) | The vans' shops alone |
| `vending` | The depots' vendors alone |
| `all` | Both |

The default is `route` because that is what the endpoint and every page on it say, so a caller that
names no scope is asking about the vans. `scope` narrows and never widens: naming a depot in
`assignedBusinessPartnerCode` under `scope=route` answers with nothing. The single-customer reads —
`/{id}/sales`, `PUT`, `DELETE` — take no scope: they name one row, whichever population it is in,
which is how vending writes its vendors through this base route.

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api/route-customers` | `customers.view` | The route customers (`scope`, see below) |
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
| `/api/route-customers` | `assignedBusinessPartnerCode`, `activeOnly` (default **true**), `scope` (default **route**) |
| `/sales-summary` | `assignedBusinessPartnerCode`, `from`, `to`, `dormantDays`, `includeInactive` (default **true**), `scope` (default **route**) |
| `/product-mix` | `assignedBusinessPartnerCode`, `routeCustomerId`, `from`, `to`, `top` (default `0`, meaning no cap), `scope` (default **route**) |

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

#### Vending

**Base route:** `/api/vending`  
**Auth:** Bearer + `ApiAccess`, roles Admin, Manager, Cashier

The vending depots are route customers seen from the other side: a depot is the business partner a
`CartVendor` account sells on, and its vendors are the route customers under that partner. One vendor
at a time is still written through `/api/route-customers` above; this is the read that puts depots,
their cashier accounts and their vendors on one page, and the bulk upload that spans depots.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/vending/overview` | `depots` (per business partner: `warehouseCodes`, `costCentreCodes`, cashier and vendor counts, `setupProblem` when active cashiers disagree on warehouse or cost centre, `vendorCodePrefix`, `nextVendorCode` and `vendorCodeProblem`), `accounts` (every CartVendor account with its business partner, cost centre, single warehouse, active vendor count, `lastLoginAt` and `setupProblem` from the same resolver a sale runs) and `vendors` (every vendor under those partners, removed ones included) |
| POST | `/api/vending/vendors/import` | Checks (`validateOnly: true`, the default) or adds a sheet of vendors. Also needs `customers.create`. Body `{ validateOnly, rows: [{ rowNumber, depot, code, name, surname, phone, email, address, vatNumber }] }`, at most 1000 rows. Answers 200 with `imported`, `createCount`, `restoreCount`, `errorCount` and a result per row (`code`, `codeIssued`, `depot`, `action` = `Create` / `Restore` / `Error`, `vendorId`, `errors`). Any row with a problem saves nothing |

Role-gated rather than on `customers.view`: the overview names every vending account and the warehouse
it draws from, and a cart vendor holds that permission to read its own vendor list, not every depot's staff.

**Vendor codes.** A vendor at a vending depot is coded with its depot's warehouse prefix and three
digits: `VMB` for KEFBYC, `VMP` for KEFGRC, `VMM` for CORMACH — `VMB001`, `VMP014`. A code is unique
across every depot. `POST /api/route-customers` and the upload both hold vendors to it: a blank code
takes the prefix's next number (after the highest ever issued, removed vendors included), and any other
code is refused with `Vending.VendorCodeDoesNotFitDepot`. A depot whose warehouse has no prefix, or
whose cashiers draw on warehouses with different ones, cannot add vendors
(`Vending.DepotCannotNumberVendors`). `PUT /api/route-customers/{id}` applies the rule only when the
code or the depot changes, so a vendor under an older code can still be edited where it is; a move to a
depot on another warehouse is refused. Van routes' shops are not affected.

In an upload, `depot` is the business partner code or the warehouse, and may be blank when `code` is
given, because the prefix names the depot. Blank codes are numbered after every code the file names.
A code matching a removed vendor at the same depot restores that vendor; one matching a live vendor is
a row error, not an update.

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
**Auth:** Bearer + `ApiAccess`; `queue/process` and `item-tax-groups` are Admin

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
| POST | `/api/Sync/item-tax-groups` | **Admin.** Copy item VAT groups from SAP now |

`/queue` and `/queue/status` are two routes on one action, not two endpoints — they answer
identically, and neither is deprecated.

`item-tax-groups` does now what the 03:45 CAT `SapItemTaxGroupWarmJob` does nightly: reads
every sellable item's VAT group from the SAP item master, bypassing the six-hour cache, into
`SapItemTaxGroups`, the table till sales are taxed from and `DesktopIntegration/tax/item-rates`
serves. It answers with the counts, each item whose group changed (`itemCode`, `was`, `now`), and
any group in use with no configured rate or tax id. A failed or empty SAP read changes nothing and
answers an error; a sync already running answers 409. Tills re-read within four hours, or on Refresh.

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
| POST | `/api/whatsapp/sessions` | Create one. Answers `201`, and registers the inbound webhook |
| POST | `/api/whatsapp/sessions/{sessionId}/start` | Start it |
| POST | `/api/whatsapp/sessions/{sessionId}/stop` | Stop it |
| GET | `/api/whatsapp/sessions/{sessionId}/qr` | The QR code to scan |
| GET | `/api/whatsapp/sessions/{sessionId}/webhook` | Whether OpenWA delivers this session's messages here |
| POST | `/api/whatsapp/sessions/{sessionId}/webhook` | Register or repair that delivery |
| POST | `/api/whatsapp/sessions/{sessionId}/messages/send-text` | Send a message |
| POST | `/api/whatsapp/sessions/{sessionId}/messages/reply` | Reply to one |
| POST | `/api/whatsapp/webhook/openwa` | **Anonymous.** Inbound from the bridge. `application/json` only, answers `202` |

The webhook is the one route on this controller outside `AdminOnly`, because the bridge is not a
user. Everything else refuses anyone who is not an admin.

A paired session delivers nothing on its own: OpenWA posts events only to webhooks registered
against that session. Creating or starting a session registers one aimed at `OpenWA:WebhookPublicUrl`
and signed with `OpenWA:WebhookSecret`, and `GET .../webhook` reports whether OpenWA is actually
holding it. When it is not, the inbox stays empty and nothing anywhere reports an error — which is
what the `Delivery` row on the operator console and `scripts/Test-WhatsAppDeliveryPath.ps1` exist to
catch.

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
**Audit:** `register`, `unregister` and `send` each write a row. The `send` row names the audience — a
user, a role, or every registered device when neither is given — and how many devices were reached,
because nothing else records that a broadcast happened.

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

**Van stock arrivals are a second data-only message.** When the transfer listener's webhook records
a line landing in a warehouse (`ProcessTransferEventHandler`), it raises `StockTransferReceivedEvent`;
`StockTransferReceivedHandler` in the van sales feature sends the active `ADR`/`Sales` accounts
assigned to that warehouse a silent push with `changeType: "VanStock"`, `warehouseCode`,
`fromWarehouse`, `transferDocEntry`, `transferDocNum` and `changedAtUtc`. One push per transfer
document, not per line — the handset re-reads its whole warehouse on the signal and reconciles its
own ledger from that, so a load booked after the van's morning position reaches the handset the next
time it has signal instead of the next time the rep pulls the stock screen down. Every data-only push
also carries `is_silent_in_foreground: "true"`, which is what keeps the handset's FCM plugin from
raising a blank tray entry for it.

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
**Audit:** `PUT {deviceId}/handset` writes `AssignFiscalDeviceHandset` naming who held the device
before and who holds it after; `PUT {deviceId}/offline-lease` writes `AssignOfflineSigningLease`. A
handover **forced** over a handset still carrying signed receipts is recorded as a failure, so it
stands out: the lease row afterwards is indistinguishable from an ordinary one.

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
| GET | `/api/fiscalisation-console/revmax` | The REVMax device and what this system has filed on it over a window: device identity and fiscal day, receipts and documents filed, documents still unfiled, value and VAT per currency, recent transactions |

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

**Audit:** every call writes exactly one row, on every path, keyed on the account id with the phone
number masked. Sign-in by password or code writes `VanSalesCustomerSignIn` or
`VanSalesCustomerSignInFailed`. `otp/request` writes `VanSalesCustomerOtpRequest` and records whether a
code was **actually** sent — the response is identical either way, so the row is the only place that
is written down. A refresh token presented after it was rotated is recorded as a failed
`VanSalesCustomerSessionRefresh` with the error `Refresh token replay`: that is the theft signal. One
row per path is deliberate — a write on only the branches that found an account would make those
branches measurably slower, and give back through the clock the answer the uniform error withholds.

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
| GET | `/api/van-sales-orders/route-load` | `salesorders.view` or `vansales.customer_orders.fulfil` | What a van has been asked to carry |
| POST | `/api/van-sales-orders/{orderId}/delivery` | `salesorders.edit` or `vansales.customer_orders.fulfil` | Record what was actually delivered |
| POST | `/api/van-sales-orders/{orderId}/convert` | `salesorders.create` or `vansales.customer_orders.fulfil` | Turn a customer's order into a sales order |

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
lists the service approver, `canAdd` when SAP shows the request approved and its draft is still open and
itself `dasApproved`, and `statusNote` in a sentence otherwise. SAP can approve a request and leave its
draft Pending; that row cannot be added and has to be raised again in SAP. The decision answer follows
the same rule: an approval that leaves the draft Pending comes back `status: "Approved"` with
`canAdd: false` and a message that says so. A decision or an add that SAP refuses comes back as
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

**Stage scope.** A role named in `CreditNoteApprovals:RoleStageScopes` sees and decides only the requests
whose current SAP stage carries one of the names configured for it. The list and its `totalCount` are
filtered in SAP; the detail, the attachment download and the decision answer
`403 CreditNoteApproval.OutsideStageScope` for any other request. `WashBay` ships scoped to
`Wash Bay Approvals` and holds `creditnotes.approve` without `creditnotes.add_approved`; if its entry is
missing it is refused the queue (`CreditNoteApproval.StageScopeNotConfigured`) rather than shown all of it,
and a configured name SAP does not have is `CreditNoteApproval.StageScopeUnresolved`. The add is not
scoped, because no scoped role may add.

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

### 52. Market Breakages

**Base route:** `/api/market-breakages`  
**Auth:** Bearer + `vansales.breakages.confirm` throughout

Broken or damaged stock a van rep collected back from shops. The rep swaps the shop's broken units
for good ones off the van and carries the broken ones home, so the van is physically short while SAP
still counts the stock as sellable. The rep reports it from the handset
(`POST /api/vansales/breakages`, which moves nothing); the office counts what comes off the van and
**confirms** it, which posts a SAP stock transfer of the counted quantities from the van's warehouse
into the returns warehouse (`MarketBreakages:ReturnsWarehouseCode`, default `RETURNS`). Or it
**rejects** the report, with a reason, and nothing moves.

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/market-breakages` | `vansales.breakages.confirm` | Reports newest first: `status` open (pending, failed or stranded), `Pending`, `Transferring`, `Transferred`, `TransferFailed`, `Rejected` or empty for all; `search` matches the rep, van, shop or an item code; `page` 1, `pageSize` 25. `statusCounts` holds every status, filters aside |
| GET | `/api/market-breakages/{id}` | `vansales.breakages.confirm` | One report: lines with the reported and confirmed quantity, who decided, the transfer's `sapDocNum`, `lastError` |
| POST | `/api/market-breakages/{id}/confirm` | `vansales.breakages.confirm` | `{ "lines": [{ "lineId": 1, "confirmedQuantity": 2 }], "remarks": "…" }` — every line, exactly once; zero drops a line; all zeros is refused (reject instead). Posts the transfer |
| POST | `/api/market-breakages/{id}/reject` | `vansales.breakages.confirm` | `{ "remarks": "…" }` — required. Nothing is transferred |

**The report's van, not the rep's current one.** The van warehouse is snapshotted when the report
arrives, so moving a rep to another van before the office confirms does not take the stock off the
wrong van. A rep with no van assigned is refused at the handset (`MarketBreakage.NoVanWarehouse`).

**Reported and confirmed are kept apart.** `reportedQuantity` is what the rep sent and never changes;
`confirmedQuantity` is the office's count and the only figure the transfer uses.

**One transfer per report.** The confirm holds a post lock keyed on the report alone (scope
`market-breakage-transfer`), so two people confirming at once get one transfer and a
`409 MarketBreakage.PostInProgress`; a confirm after the transfer replays it. The report is claimed as
`Transferring` in one conditional update before SAP is called, which is also what stops a reject landing
mid-transfer. From the claim on the request's token is dropped, so a closed tab cannot strand it.

**Failures are retried by confirming again.** A SAP refusal, short stock on the van
(`MarketBreakage.InsufficientStock`), or stock SAP could not read leaves the report `TransferFailed` with
`lastError`, and the office may correct the count on the retry. A SAP timeout is recorded the same way
but says the transfer may exist — check SAP before confirming again. A report read back as
`Transferring` was stranded mid-post; confirming finishes it once the lock has expired.


---

### 53. Stock Write-offs

**Base route:** `/api/stock-write-offs`  
**Auth:** Bearer + `stock.writeoffs.view` to read, `stock.writeoffs.post` to write anything off

Counted stock issued out of a warehouse as a SAP **goods issue** (`InventoryGenExits`) — the one
document that takes stock off SAP's books with no business partner on the other side of it. Every
other path here either sells stock, buys it, or moves it between warehouses, so a warehouse that only
ever receives (the returns warehouse market breakages transfer into) had no way to be drained.

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/stock-write-offs` | `stock.writeoffs.view` | Write-offs newest first: `status` `Pending`, `Posting`, `Posted`, `PostFailed`, `Cancelled` or empty for all; `warehouseCode`; `page` 1, `pageSize` 25 (max 200). `statusCounts` holds every status, filters aside |
| GET | `/api/stock-write-offs/reasons` | `stock.writeoffs.view` | The reasons a write-off may carry, and `recordedInSap` — see **Where the reason comes from** below |
| GET | `/api/stock-write-offs/{id}` | `stock.writeoffs.view` | One write-off: its lines with batch or serial, who raised it, the goods issue's `sapDocNum`, `lastError` |
| POST | `/api/stock-write-offs` | `stock.writeoffs.post` | `{ "warehouseCode": "RETURNS", "reason": "Breakage", "remarks": "…", "docDate": "2026-09-20", "clientRequestId": "…", "lines": [{ "itemCode": "…", "quantity": 4, "batchNumber": "B-0099" }] }`. `201` with the posted write-off, or `200` when a resend replays one already posted |

**A line is one thing counted.** An item, and the batch it came out of where SAP manages the item that
way. Five of one batch and three of another are two lines, because that is how they were counted and
how SAP records them. A serial-managed line names one serial number and is one unit.

**A batch-managed line must name its batch.** SAP refuses the *whole document* when one does not, so
nineteen good lines are lost with the twentieth. The API refuses such a line before anything is sent.

**SAP decides what the write-off is worth.** No `AccountCode` and no price are sent, so SAP's own item
and warehouse G/L determination charges the issue and values it at the item's cost — exactly as it
would for a goods issue keyed into B1 by hand. There is no `CardCode`: it is not a business-partner
document.

**Where the reason comes from.** A user field belongs to a table in Business One, so whether a reason
can be recorded in SAP at all depends on whether this company database defines one on the goods-issue
line table (`IGE1`). Where it does, `GET /reasons` returns SAP's own valid values and `recordedInSap`
is true — SAP rejects any value its field does not carry, so the picker and the payload must come from
the same place. Where it does not, the configured list (`StockWriteOffs:Reasons`) is offered,
`recordedInSap` is false, and the reason is kept on the local record and in the document's `Comments`.

**One goods issue per write-off.** `clientRequestId` (or the `Idempotency-Key` header) identifies the
count: resending it returns the write-off already raised rather than issuing the stock twice, and is
how a failed post is retried. On top of that the post holds a lock keyed on the record alone (scope
`stock-write-off-post`), so two people posting at once get one goods issue and a
`409 StockWriteOff.PostInProgress`. The record is claimed as `Posting` in one conditional update before
SAP is called, and from the claim on the request's token is dropped, so a closed tab cannot strand it.
A `clientRequestId` another account already used is a `409 StockWriteOff.DuplicateRequest`.

**Stock SAP could not be read is not written off.** Short stock is a
`StockWriteOff.InsufficientStock`; a warehouse SAP would not answer for is a `StockWriteOff.PostFailed`
that says so. Unread is not the same as zero, and the difference decides whether stock is destroyed, so
the check fails closed either way. A SAP timeout leaves `PostFailed` saying the goods issue may exist —
check SAP before posting again.

**The local ledger is not moved.** A write-off out of a warehouse the daily stock ledger monitors
leaves that ledger overstating it until `StockLedgerDivergenceJob` corrects it, within the hour. The
returns warehouse is in neither `DailyStock:MonitoredWarehouses` nor `ReconcileWarehouses`, so a
write-off out of it needs nothing local at all.

**There is no reversal here.** A posted goods issue is cancelled in B1, or offset with a goods receipt.


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
  "eventType": "invoice.cancelled",
  "category": "Invoice",
  "description": "An invoice was cancelled and reversed by a credit note."
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
