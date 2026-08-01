using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ShopInventory.Tests")]

// So SapStatementQueryTests can prove SAP accepts the handler's *own* SQL constants rather than a
// copy of them. A copy would let the shipped statement drift away from the one the integration test
// established the Service Layer accepts, which is the single thing that test exists to pin down.
[assembly: InternalsVisibleTo("ShopInventory.IntegrationTests")]
