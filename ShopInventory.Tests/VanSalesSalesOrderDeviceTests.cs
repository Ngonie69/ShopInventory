using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Mobile;
using ShopInventory.Controllers;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Which handset a van sales sales order was captured on.
/// </summary>
/// <remarks>
/// <para>The handset has named itself on an <c>X-Device-Model</c> header of every request since
/// August — "Samsung SM-A055F Musa's phone" — and nowhere in the order body. The sales-order endpoint
/// never read the header, so every van sales order reached the mobile orders page as "Device not
/// captured".</para>
/// </remarks>
public sealed class VanSalesSalesOrderDeviceTests
{
    private static readonly VanSalesCustomerResolution Customer = new("C-VAN-014", new RouteCustomerEntity
    {
        Id = 41,
        Code = "VAN008",
        Name = "Test Shop"
    });

    [Fact]
    public void The_endpoint_reads_the_header_the_handset_sends()
    {
        // The name is a contract with KefalosVanSales' HttpService. A typo here binds null forever and
        // nothing fails, which is how this went missing in the first place.
        var parameter = typeof(VanSalesCompatibilityController)
            .GetMethod(nameof(VanSalesCompatibilityController.CreateSalesOrder))!
            .GetParameters()
            .Single(p => p.ParameterType == typeof(string));

        var fromHeader = parameter.GetCustomAttribute<FromHeaderAttribute>();

        Assert.NotNull(fromHeader);
        Assert.Equal("X-Device-Model", fromHeader.Name);
    }

    [Fact]
    public void The_device_is_stored_on_the_order()
    {
        var mapped = Map("Samsung SM-A055F Musa's phone");

        Assert.Equal("Samsung SM-A055F Musa's phone", mapped.DeviceInfo);
    }

    [Fact]
    public void An_order_from_a_handset_that_sends_no_header_still_maps()
    {
        Assert.Null(Map(null).DeviceInfo);
        Assert.Null(Map("   ").DeviceInfo);
    }

    [Fact]
    public void A_long_device_name_is_held_to_the_column_rather_than_failing_the_save()
    {
        var mapped = Map("  " + new string('x', 250) + "  ");

        Assert.Equal(200, mapped.DeviceInfo!.Length);
    }

    private static CreateSalesOrderRequest Map(string? deviceInfo) =>
        VanSalesCompatibilityMapper.MapSalesOrderRequest(
            new VanSalesOrderRequest
            {
                Type = "SO",
                Reference = "Test Shop",
                VanOrder = "VAN005-SO-20260917-A9E0A6",
                Currency = "USD",
                DueDate = "2026-09-17",
                Items = []
            },
            Customer,
            "VAN005",
            "CC-VAN",
            deviceInfo);
}
