using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
namespace Ecommerce.Features.Orders.Services;

public static class OrderRules
{
    public static bool MatchesRequest(
        IReadOnlyCollection<OrderItem> savedItems,
        IReadOnlyCollection<CreateOrderItemRequest> requestedItems)
    {
        if (savedItems.Count != requestedItems.Count)
            return false;

        foreach (var requestedItem in requestedItems)
        {
            var savedItem = savedItems.FirstOrDefault(
                item => item.ProductId == requestedItem.ProductId);
            if (savedItem is null || savedItem.Quantity != requestedItem.Quantity)
                return false;
        }

        return true;
    }

    public static long CalculateTotalCents(
        IEnumerable<OrderItem> savedItems)
    {
        long total = 0;
        foreach (var item in savedItems)
            total += item.UnitPriceCents * item.Quantity;
        return total;
    }
}
