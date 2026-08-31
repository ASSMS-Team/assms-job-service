namespace JobService.Messaging.Producers;

public interface IEventPublisher
{
    // The implementation builds the envelope around payload, so callers pass
    // only what varies. key is the message key - jobId on every ASSMS topic,
    // because Kafka orders within a partition and routes by key hash, so every
    // event about one job lands on one partition in order.
    //
    // eventVersion is a parameter rather than a constant on the publisher
    // because each event type versions its own payload independently.
    Task PublishAsync(string topic, string eventType, int eventVersion, string key, object payload);
}
