using LegacyOrders.Domain;

namespace LegacyOrders.Services;

public class PricingCalculator
{
    public const decimal VipDiscountRate = 0.05m;
    public const decimal BulkDiscountThreshold = 1000m;
    public const decimal BulkDiscountRate = 0.02m;

    public decimal CalculateDiscount(Order order, Customer customer)
    {
        decimal discount = 0m;
        if (customer.IsVip)
            discount += order.Subtotal * VipDiscountRate;
        if (order.Subtotal > BulkDiscountThreshold)
            discount += Math.Round(order.Subtotal * BulkDiscountRate, 2, MidpointRounding.AwayFromZero);
        return discount;
    }

    public decimal CalculateTotal(Order order)
    {
        var subtotal = order.Lines.Sum(l => l.Quantity * l.UnitPrice);
        order.Subtotal = subtotal;
        return Math.Round(subtotal - order.Discount, 2);
    }
}
