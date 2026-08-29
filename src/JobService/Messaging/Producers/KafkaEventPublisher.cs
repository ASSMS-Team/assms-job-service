using System.Text.Json;

using Confluent.Kafka;

using JobService.Messaging.Contracts;

namespace JobService.Messaging.Producers;

// Publishes ASSMS events to Kafka as JSON, keyed by jobId.
//
// Registered as a singleton: building a producer opens sockets and starts a
// background thread, so one per request would be ruinous, and IProducer is
// thread-safe by design.
public class KafkaEventPublisher : IEventPublisher, IDisposable
{
    // The repository service name, as the naming convention in the contract
    // doc requires.
    private const string ProducerName = "job-service";

    // camelCase to match the contract: the REST APIs already serialize this
    // way, and a consumer should not have to switch conventions between them.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(string bootstrapServers, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            // message.timeout.ms - the whole budget for a message, retries
            // included, before ProduceAsync faults. Ten seconds rather than the
            // five-minute default: the publish sits inside a request the Agent
            // is waiting on, so a broker that is down has to be given up on in
            // a time a person will wait.
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(string topic, string eventType, int eventVersion, string key, object payload)
    {
        var envelope = new EventEnvelope<object>
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = eventType,
            EventVersion = eventVersion,
            // Kind Utc is what makes System.Text.Json write the trailing Z, so
            // the timestamp goes out as ISO 8601 UTC rather than an unmarked
            // local time.
            OccurredAt = DateTime.UtcNow,
            Producer = ProducerName,
            Payload = payload
        };

        var value = JsonSerializer.Serialize(envelope, SerializerOptions);

        var message = new Message<string, string>
        {
            // The plain UUID string, not JSON - the key is a string, only the
            // value is serialized JSON.
            Key = key,
            Value = value
        };

        var result = await _producer.ProduceAsync(topic, message);

        _logger.LogInformation(
            "Published {EventType} for key {Key} to {TopicPartitionOffset}.",
            eventType,
            key,
            result.TopicPartitionOffset);
    }

    public void Dispose()
    {
        // Messages are queued in the background, so without the flush a
        // shutdown between the last publish and process exit would drop them.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();

        GC.SuppressFinalize(this);
    }
}
