using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Xunit;

namespace Ecommerce.Api.Tests.Orders;

public class OrderRulesTests
{
    [Fact]
    public void SameItemsInDifferentOrderAreAnIdempotentReplay()
    {
        var saved = new List<OrderItem>
        {
            new() { ProductId = 10, Quantity = 2 },
            new() { ProductId = 20, Quantity = 1 }
        };
        var request = new CreateOrderRequest([new(20, 1), new(10, 2)]);

        Assert.True(OrderRules.MatchesRequest(saved, request.Items));
    }

    [Fact]
    public void ChangedQuantityIsNotAnIdempotentReplay()
    {
        var saved = new List<OrderItem> { new() { ProductId = 10, Quantity = 2 } };
        var request = new CreateOrderRequest([new(10, 3)]);

        Assert.False(OrderRules.MatchesRequest(saved, request.Items));
    }

    [Fact]
    public void ReplacedProductIsNotAnIdempotentReplay()
    {
        var saved = new List<OrderItem> { new() { ProductId = 10, Quantity = 2 } };
        var request = new CreateOrderRequest([new(11, 2)]);

        Assert.False(OrderRules.MatchesRequest(saved, request.Items));
    }

    [Fact]
    public void AddedProductIsNotAnIdempotentReplay()
    {
        var saved = new List<OrderItem> { new() { ProductId = 10, Quantity = 2 } };
        var request = new CreateOrderRequest([new(10, 2), new(11, 1)]);

        Assert.False(OrderRules.MatchesRequest(saved, request.Items));
    }

    [Fact]
    public void PaymentTotalUsesSavedLinePricesAndQuantities()
    {
        var saved = new List<OrderItem>
        {
            new() { ProductId = 10, UnitPriceCents = 750, Quantity = 2 },
            new() { ProductId = 20, UnitPriceCents = 400, Quantity = 1 }
        };

        Assert.Equal(1900, OrderRules.CalculateTotalCents(saved));
    }

    [Fact]
    public void PaymentTotalSupportsAmountsLargerThanIntMaxValue()
    {
        var saved = new List<OrderItem>
        {
            new() { ProductId = 10, UnitPriceCents = 2_000_000_000, Quantity = 2 }
        };

        Assert.Equal(4_000_000_000L, OrderRules.CalculateTotalCents(saved));
    }
}
