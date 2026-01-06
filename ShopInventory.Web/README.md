# Shop Inventory Web Application

A Blazor Web App that consumes the ShopInventory API for invoicing and inventory management.

## Features

- **Authentication**: JWT-based login/logout with token refresh
- **Dashboard**: Overview of invoices, payments, and quick actions
- **Invoices**: View, search, and create invoices
- **Inventory Transfers**: Track inventory movements between warehouses
- **Incoming Payments**: View and search customer payments
- **Products**: Browse products in warehouses with batch information
- **Price List**: View item prices in USD and ZIG currencies

## Prerequisites

- .NET 10.0 SDK
- ShopInventory API running on port 5106

## Running the Application

### 1. Start the API (ShopInventory)

```bash
cd ShopInventory
dotnet run
```

The API will start on `http://localhost:5106`

### 2. Start the Web Application (ShopInventory.Web)

In a separate terminal:

```bash
cd ShopInventory.Web
dotnet run
```

The web application will start on `http://localhost:5051`

### 3. Access the Application

Open your browser and navigate to `http://localhost:5051`

## Configuration

The API URL can be configured in `appsettings.json`:

```json
{
  "ApiSettings": {
    "BaseUrl": "http://localhost:5106/"
  }
}
```

## Project Structure

```
ShopInventory.Web/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor      # Main layout with navigation
│   │   └── NavMenu.razor         # Side navigation menu
│   ├── Pages/
│   │   ├── Home.razor            # Dashboard
│   │   ├── Login.razor           # Login page
│   │   ├── Invoices.razor        # Invoice list
│   │   ├── CreateInvoice.razor   # Invoice creation
│   │   ├── InventoryTransfers.razor
│   │   ├── Payments.razor
│   │   ├── Products.razor
│   │   └── Prices.razor
│   ├── App.razor
│   └── Routes.razor
├── Models/                       # DTOs matching the API
│   ├── AuthModels.cs
│   ├── InvoiceModels.cs
│   ├── InventoryTransferModels.cs
│   ├── PaymentModels.cs
│   ├── ProductModels.cs
│   └── PriceModels.cs
├── Services/                     # API client services
│   ├── AuthService.cs
│   ├── CustomAuthStateProvider.cs
│   ├── InvoiceService.cs
│   ├── InventoryTransferService.cs
│   ├── PaymentService.cs
│   ├── ProductService.cs
│   └── PriceService.cs
└── Program.cs                    # Application entry point
```

## Authentication

The application uses JWT tokens for authentication. Tokens are stored in local storage and automatically attached to API requests. The auth state provider handles token refresh when needed.

### Default Login Credentials

Use the credentials configured in your ShopInventory API (typically `admin`/`admin123` for development).

## API Endpoints Consumed

| Feature | Endpoints |
|---------|-----------|
| Auth | POST /api/auth/login, POST /api/auth/refresh, POST /api/auth/logout |
| Invoices | GET/POST /api/invoice, GET /api/invoice/{docEntry}, GET /api/invoice/customer/{cardCode}, GET /api/invoice/date/{date} |
| Inventory Transfers | GET /api/inventorytransfer/{warehouseCode}, GET /api/inventorytransfer/{warehouseCode}/paged |
| Payments | GET /api/incomingpayment, GET /api/incomingpayment/{docEntry}, GET /api/incomingpayment/date/{date} |
| Products | GET /api/product/warehouse/{warehouseCode}, GET /api/product/{itemCode}/batches/{warehouseCode} |
| Prices | GET /api/price, GET /api/price/grouped, GET /api/price/item/{itemCode} |
