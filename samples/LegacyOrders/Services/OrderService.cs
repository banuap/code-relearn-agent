using LegacyOrders.Data;
using LegacyOrders.Domain;
using LegacyOrders.Messaging;
using LegacyOrders.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LegacyOrders.Services;

public interface IOrderService
{
    Task<Order?> GetAsync(int id);
    Task<Order> CreateAsync(int customerId, List<OrderLine> lines);
    Task SubmitAsync(int orderId);
    Task ChangeStatusAsync(int orderId, string action);
}

public class OrderService : IOrderService
{
    private readonly OrdersDbContext _db;
    private readonly ICustomerRepository _customers;
    private readonly PricingCalculator _pricing;
    private readonly IOrderEventPublisher _events;
    private readonly IMemoryCache _cache;

    public OrderService(OrdersDbContext db, ICustomerRepository customers, PricingCalculator pricing, IOrderEventPublisher events, IMemoryCache cache)
    { _db = db; _customers = customers; _pricing = pricing; _events = events; _cache = cache; }

    public async Task<Order?> GetAsync(int id)
    {
        if (_cache.TryGetValue($"order:{id}", out Order? cached)) return cached;
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == id);
        if (order != null) _cache.Set($"order:{id}", order, TimeSpan.FromMinutes(5));
        return order;
    }

    public async Task<Order> CreateAsync(int customerId, List<OrderLine> lines)
    {
        if (lines.Count == 0) throw new InvalidOperationException("An order must contain at least one line.");
        var customer = await _db.Customers.FindAsync(customerId) ?? throw new KeyNotFoundException("Customer not found");
        var order = new Order { CustomerId = customerId, Lines = lines, CreatedOn = DateTime.UtcNow, Status = OrderStatus.Draft };
        order.Total = _pricing.CalculateTotal(order);
        order.Discount = _pricing.CalculateDiscount(order, customer);
        order.Total = _pricing.CalculateTotal(order);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    public async Task SubmitAsync(int orderId)
    {
        var order = await _db.Orders.FirstAsync(o => o.OrderId == orderId);
        if (order.Status != OrderStatus.Draft) throw new InvalidOperationException("Only draft orders can be submitted.");
        var available = await _customers.GetAvailableCreditAsync(order.CustomerId) ?? 0m;
        if (order.Total > available) throw new InvalidOperationException("Order exceeds the customer's available credit.");
        order.Status = OrderStatus.Submitted;
        await _db.SaveChangesAsync();
        _cache.Remove($"order:{orderId}");
        await _events.OrderSubmittedAsync(order);
    }

    public async Task ChangeStatusAsync(int orderId, string action)
    {
        var order = await _db.Orders.FirstAsync(o => o.OrderId == orderId);
        switch (action)
        {
            case "approve":
                if (order.Status != OrderStatus.Submitted) throw new InvalidOperationException("Only submitted orders can be approved.");
                order.Status = OrderStatus.Approved;
                break;
            case "ship":
                order.Status = OrderStatus.Shipped;
                break;
            case "cancel":
                if (order.Status == OrderStatus.Shipped) throw new InvalidOperationException("Shipped orders cannot be cancelled.");
                order.Status = OrderStatus.Cancelled;
                break;
            default:
                throw new ArgumentException($"Unknown action {action}");
        }
        await _db.SaveChangesAsync();
    }
}
