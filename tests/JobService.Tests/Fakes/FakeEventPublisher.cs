using JobService.Messaging.Producers;

namespace JobService.Tests;

// Stands in for Kafka. Records the whole call, because what is published is as
// much of the contract as whether it was published at all - the topic, the key
// and the payload are what Dispatch and Reporting actually read.
public class FakeEventPublisher : IEventPublisher
{
    public string? PublishedTopic;
    public string? PublishedEventType;
    public int PublishedEventVersion;
    public string? PublishedKey;
    public object? PublishedPayload;
    public int PublishAsyncCallCount;

    // Left null for the happy path. Set it to make publishing fail, which is
    // what the criterion-5 test needs: a broker that is down must not cost the
    // Agent a job that is already in the database.
    public Exception? ExceptionToThrow;

    public Task PublishAsync(string topic, string eventType, int eventVersion, string key, object payload)
    {
        // Recorded and counted before the throw, so the failure test can still
        // assert that publishing was attempted with the right message.
        PublishedTopic = topic;
        PublishedEventType = eventType;
        PublishedEventVersion = eventVersion;
        PublishedKey = key;
        PublishedPayload = payload;
        PublishAsyncCallCount++;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
