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

---

### 3. Two-Factor Authentication

**Base route:** `/api/TwoFactor`

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/TwoFactor/status` | Get current 2FA status for the authenticated user |
| POST | `/api/TwoFactor/setup` | Initiate 2FA setup, returns secret key + QR code URI |
| POST | `/api/TwoFactor/verify` | Verify TOTP code and enable 2FA |
| POST | `/api/TwoFactor/disable` | Disable 2FA |

**All endpoints require Bearer authentication.**

**Setup Response:**

```json
{
  "secretKey": "BASE32SECRET",
  "qrCodeUri": "otpauth://totp/ShopInventory:user?secret=...",
  "manualEntryKey": "XXXX XXXX XXXX XXXX",
  "backupCodes": ["code1", "code2", "..."]
}
```

---

### 4. Users

**Base route:** `/api/User`  
**Auth:** Bearer + Admin role

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/User` | List all users (paginated, searchable) |
| GET | `/api/User/{id}` | Get user by ID |
| PUT | `/api/User/{id}` | Update user details |
| POST | `/api/User/{id}/change-password` | Admin-initiated password change |
| POST | `/api/User/{id}/unlock` | Unlock a locked-out account |

**GET list query parameters:** `page`, `pageSize`, `search`

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
| POST | `/api/UserManagement` | `users.create` | Create user with granular permissions |
| PUT | `/api/UserManagement/{id}` | `users.edit` | Update user + permissions |
| DELETE | `/api/UserManagement/{id}` | `users.delete` | Delete user |

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
| GET | `/api/UserActivity/dashboard` | `audit.view` | System-wide activity dashboard |
| GET | `/api/UserActivity/user/{userId}` | `audit.view` | Specific user's activity summary |
| GET | `/api/UserActivity/me` | ApiAccess | Current user's own activity |

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
| GET | `/api/Product/warehouse/{warehouseCode}` | Products in a specific warehouse with batch info |
| GET | `/api/Product/{itemCode}` | Get a single product |
| GET | `/api/Product/{itemCode}/batches` | Get batch information for a product |

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
| GET | `/api/Stock/warehouse/{code}` | Get all stock in a specific warehouse |
| GET | `/api/Stock/batch/{warehouseCode}/{itemCode}` | Get batch detail for an item in a warehouse |

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
| GET | `/api/Price/by-customer/{cardCode}` | Customer-specific pricing |
| POST | `/api/Price/sync` | Force price sync from SAP (Admin only) |

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
**Auth:** Bearer + permissions as noted

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| POST | `/api/Invoice` | Admin or Cashier role | Create a new invoice |
| GET | `/api/Invoice` | `invoices.view` | List invoices (paginated) |
| GET | `/api/Invoice/{id}` | `invoices.view` | Get invoice by ID |
| GET | `/api/Invoice/docnum/{docNum}` | `invoices.view` | Get invoice by SAP DocNum |
| GET | `/api/Invoice/date-range` | `invoices.view` | Get invoices by date range |
| PUT | `/api/Invoice/{id}` | `invoices.edit` | Update a draft invoice |
| DELETE | `/api/Invoice/{id}` | `invoices.delete` | Delete a draft invoice |
| POST | `/api/Invoice/confirm` | `invoices.create` | Confirm and post invoice to SAP |

**Query parameters for GET list:** `page`, `pageSize`, `status`, `cardCode`, `fromDate`, `toDate`, `warehouseCode`

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

**Query parameters:** `page`, `pageSize`, `status`, `cardCode`, `fromDate`, `toDate`

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
| GET | `/api/SalesOrder` | `invoices.view` | List local sales orders |
| GET | `/api/SalesOrder/{id}` | `invoices.view` | Get by ID |
| GET | `/api/SalesOrder/number/{orderNumber}` | `invoices.view` | Get by order number |
| POST | `/api/SalesOrder` | `invoices.create` | Create sales order |
| PUT | `/api/SalesOrder/{id}` | `invoices.edit` | Update sales order |

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

**Credit control endpoint**

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/credit-control/over-limit` | `customers.view` | Accounts and groups currently over their credit limit |

Same finding as the evening review, on demand and in full. Served from a 10-minute cache; pass
`?refresh=true` to re-read SAP, which is what to do after taking a payment. Concurrent callers
share one sweep rather than each triggering their own.

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

| Method | Endpoint | Permission | Description |
|--------|----------|-----------|-------------|
| GET | `/api/Quotation` | `invoices.view` | List local quotations |
| GET | `/api/Quotation/sap` | `invoices.view` | List SAP quotations |
| GET | `/api/Quotation/{id}` | `invoices.view` | Get by ID |
| POST | `/api/Quotation` | `invoices.create` | Create quotation |
| PUT | `/api/Quotation/{id}` | `invoices.edit` | Update quotation |

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
| GET | `/api/PurchaseOrder/{id}` | `purchasing.view` | Get by ID |
| POST | `/api/PurchaseOrder` | `purchasing.create` | Create purchase order |
| PUT | `/api/PurchaseOrder/{id}` | `purchasing.edit` | Update purchase order |
| POST | `/api/PurchaseOrder/{id}/receive` | `purchasing.receive` | Receive goods |

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
| GET | `/api/IncomingPayment/{docEntry}` | Get by SAP DocEntry |
| GET | `/api/IncomingPayment/docnum/{docNum}` | Get by document number |
| GET | `/api/IncomingPayment/by-invoice/{invoiceDocEntry}` | Payments for an invoice |
| GET | `/api/IncomingPayment/customer/{cardCode}` | Customer's payments (paginated) |

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

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/Payment/providers` | Get available payment providers |
| POST | `/api/Payment/initiate` | Initiate a payment transaction |
| GET | `/api/Payment/{id}/status` | Check payment status |
| GET | `/api/Payment/transactions` | Transaction history (paginated, filterable) |

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
| GET | `/api/InventoryTransfer/{docEntry}` | Get transfer details |
| GET | `/api/InventoryTransfer/status/{docEntry}` | Get posting status |
| GET | `/api/InventoryTransfer/requests` | List transfer requests, newest first (`page`, `pageSize`, `status`) |
| PATCH | `/api/InventoryTransfer/request/{docEntry}` | Change an open request's lines and warehouses. Admin, StockController, DepotController, Manager |
| POST | `/api/InventoryTransfer/request/{docEntry}/convert` | Authorize a request and generate the SAP transfer. Admin, StockController, DepotController |
| POST | `/api/InventoryTransfer/request/{docEntry}/close` | Close a request in SAP without converting it. Admin, StockController, DepotController |
| GET | `/api/InventoryTransfer/request-edits` | List changes held for approval (`status`, `requestDocEntry`, `page`, `pageSize`) |
| POST | `/api/InventoryTransfer/request-edits/{id}/decision` | Approve or reject a held change |
| POST | `/api/InventoryTransfer/request-edits/{id}/cancel` | Withdraw a change the caller proposed |

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
| GET | `/api/BusinessPartner/search?query={query}` | Search by code or name |
| GET | `/api/BusinessPartner/{cardCode}` | Get specific business partner |

**Card Types:** `cCustomer`, `cSupplier`, `cLead`

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
| GET | `/api/Document/templates` | List all templates (filter by `?type=Invoice`) |
| GET | `/api/Document/templates/{id}` | Get template by ID |
| GET | `/api/Document/templates/default/{documentType}` | Get default template for a document type |
| POST | `/api/Document/templates` | Create template (Admin/Manager) |
| PUT | `/api/Document/templates/{id}` | Update template |

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
| GET | `/api/Document/attachments/{entityType}/{entityId}` | List attachments for an entity |
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
| GET | `/api/Report/low-stock` | Low stock alert items |
| GET | `/api/Report/aging-analysis` | Customer aging analysis |
| GET | `/api/Report/inventory-value` | Inventory valuation report |
| GET | `/api/Report/item-volume-sales` | Net quantity, converted volume and net revenue per item and business partner |

**Common query parameters:** `fromDate`, `toDate`, `warehouseCode`, `top` (for top N)

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

#### GET `/api/Statement/generate/{cardCode}`

Generate a customer account statement PDF.

**Query parameters:** `fromDate`, `toDate`

**Response:** PDF file download or statement data with aging buckets.

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
| POST | `/api/Backup` | `backups.create` | Create new backup |

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
| GET | `/api/RateLimit` | `users.edit` | List all rate limits |
| GET | `/api/RateLimit/client/{clientId}` | `users.edit` | Get client's rate limit info |
| GET | `/api/RateLimit/current` | ApiAccess | Get current request's rate limit status |
| GET | `/api/RateLimit/check` | ApiAccess | Check if request would be allowed (non-incrementing) |

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
| GET | `/api/DesktopIntegration/reservations/{reservationId}` | Get reservation details |
| PUT | `/api/DesktopIntegration/reservations/{reservationId}/confirm` | Confirm and post to SAP |
| DELETE | `/api/DesktopIntegration/reservations/{reservationId}` | Cancel reservation |

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
| POST | `/api/DesktopIntegration/invoices/queue` | Queue invoice for async posting |
| GET | `/api/DesktopIntegration/invoices/queue-status/{queueId}` | Check queue status |

#### Batch Validation

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/DesktopIntegration/validate-batch-allocation` | Validate batch allocations (FIFO/FEFO) |

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
| Device configuration (QR base URL, serial, active taxes) | `GET /api/fiscal-config?deviceId=` |

Authentication is an `X-API-Key` header. The key is configured as `Fiscalisation__ApiKey` and needs the
`receipt.submit`, `sap.fiscalise` and `device.read` scopes, and no device allowlist — a device-scoped key
forces an explicit device id on every call, which breaks failover.

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
