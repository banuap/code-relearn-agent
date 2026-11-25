using Confluent.Kafka;
using LegacyOrders.Data;
using LegacyOrders.Messaging;
using LegacyOrders.Repositories;
using LegacyOrders.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddDbContext<OrdersDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Orders")));
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddSingleton<PricingCalculator>();
builder.Services.AddSingleton<IOrderEventPublisher, KafkaOrderEventPublisher>();
builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] }).Build());

var app = builder.Build();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok("ok"));
app.MapGet("/api/customers/{id:int}/credit", async (int id, ICustomerRepository repo) =>
    await repo.GetAvailableCreditAsync(id) is decimal credit ? Results.Ok(credit) : Results.NotFound());
app.Run();
