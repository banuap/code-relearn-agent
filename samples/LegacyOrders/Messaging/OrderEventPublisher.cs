using System.Text.Json;
using Confluent.Kafka;
using LegacyOrders.Domain;

namespace LegacyOrders.Messaging;

public interface IOrderEventPublisher { Task OrderSubmittedAsync(Order order); }

public class KafkaOrderEventPublisher : IOrderEventPublisher
{
    private const string Topic = "orders.submitted";
    private readonly IProducer<string, string> _producer;
    public KafkaOrderEventPublisher(IProducer<string, string> producer) => _producer = producer;

    public Task OrderSubmittedAsync(Order order) =>
        _producer.ProduceAsync(Topic, new Message<string, string> { Key = order.OrderId.ToString(), Value = JsonSerializer.Serialize(order) });
}
