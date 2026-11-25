using System.ComponentModel.DataAnnotations;
using LegacyOrders.Domain;
using LegacyOrders.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegacyOrders.Controllers;

public record CreateOrderRequest([Range(1, int.MaxValue)] int CustomerId, [MinLength(1)] List<OrderLine> Lines);

[ApiController]
[Route("api/orders")]
[Authorize(Roles = "Sales,Manager")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    public OrdersController(IOrderService orders) => _orders = orders;

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var order = await _orders.GetAsync(id);
        if (order == null) return NotFound();
        return Ok(order);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderRequest request)
    {
        if (request.Lines.Any(l => l.Quantity > 500)) return BadRequest("Quantities above 500 require a bulk order.");
        var order = await _orders.CreateAsync(request.CustomerId, request.Lines);
        return CreatedAtAction(nameof(Get), new { id = order.OrderId }, order);
    }

    [HttpPost("{id:int}/submit")]
    public async Task<IActionResult> Submit(int id)
    {
        await _orders.SubmitAsync(id);
        return NoContent();
    }

    [HttpPost("{id:int}/status/{command}")]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> ChangeStatus(int id, string command)
    {
        await _orders.ChangeStatusAsync(id, command);
        return NoContent();
    }
}
