using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LegacyOrders.Domain;

public enum OrderStatus { Draft, Submitted, Approved, Shipped, Cancelled }

[Table("tblOrder")]
public class Order
{
    [Key] public int OrderId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime CreatedOn { get; set; }
    public OrderStatus Status { get; set; }
    [Range(0, 1_000_000)] public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public bool IsDeleted { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

public class OrderLine
{
    public int OrderLineId { get; set; }
    public int OrderId { get; set; }
    [Required, StringLength(20)] public string Sku { get; set; } = "";
    [Range(1, 999, ErrorMessage = "Quantity must be between 1 and 999")] public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class Customer
{
    public int CustomerId { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [EmailAddress] public string? Email { get; set; }
    public bool IsVip { get; set; }
    public decimal CreditLimit { get; set; }
    public List<Order> Orders { get; set; } = new();
}
