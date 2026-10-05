using Ecommerce.Dtos;

namespace Ecommerce;

public static class OrderRules
{
    public static bool MatchesRequest(
        IReadOnlyCollection<OrderItem> savedItems,
        IReadOnlyCollection<CreateOrderItem> requestedItems)
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
