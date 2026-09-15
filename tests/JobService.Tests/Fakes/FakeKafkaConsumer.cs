using Confluent.Kafka;

namespace JobService.Tests;

// Stands in for the Kafka consumer so the consume loop can be run without a
// broker. It records the two calls the loop's failure handling actually turns on
// - Commit and Seek - because which of the two happens is the whole behaviour
// under test.
//
// IConsumer is a wide interface and the loop uses six of its members. The rest
// throw rather than returning something harmless: a test that starts depending on
// one should fail loudly here instead of quietly passing against a stub.
public class FakeKafkaConsumer : IConsumer<string, string>
{
    public const string Topic = "job-assigned";

    private readonly Queue<ConsumeResult<string, string>> _messages = new();

    public readonly List<string> SubscribedTopics = new();
    public readonly List<TopicPartitionOffset> CommittedOffsets = new();
    public readonly List<TopicPartitionOffset> SeekedOffsets = new();

    public int ConsumeCallCount;
    public int CloseCallCount;

    // Thrown from the next Consume instead of a message. Used to stand in for a
    // broker-level failure, which is neither a bad message nor a failed write.
    public Exception? ConsumeExceptionToThrow;

    // Runs when the queue is exhausted, before the loop is stopped. The tests use
    // it to cancel the host token, which is how a run ends: the real loop only
    // stops when the host does.
    public Action? OnMessagesExhausted;

    // Queues a message the loop will be handed, at its own offset. Offsets ascend
    // from zero so a Commit or a Seek can be tied to the message it came from.
    public ConsumeResult<string, string> Enqueue(string value, string key = "9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77")
    {
        var result = new ConsumeResult<string, string>
        {
            Topic = Topic,
            Partition = new Partition(0),
            Offset = new Offset(_messages.Count),
            Message = new Message<string, string> { Key = key, Value = value },
            IsPartitionEOF = false
        };

        _messages.Enqueue(result);

        return result;
    }

    public ConsumeResult<string, string> Consume(CancellationToken cancellationToken = default)
    {
        ConsumeCallCount++;

        // Checked first so a token the test cancelled mid-message stops the loop
        // here rather than after another message has been handed out.
        cancellationToken.ThrowIfCancellationRequested();

        if (ConsumeExceptionToThrow is not null)
        {
            var toThrow = ConsumeExceptionToThrow;
            // Cleared so the loop's "log it and read again" path does not spin on
            // the same failure forever.
            ConsumeExceptionToThrow = null;

            throw toThrow;
        }

        if (_messages.Count == 0)
        {
            OnMessagesExhausted?.Invoke();

            // The loop's own exit: the host stopping is what ends a real run, and
            // Consume is where it is noticed.
            throw new OperationCanceledException();
        }

        var next = _messages.Dequeue();

        LastConsumedMessage = next.Message;

        return next;
    }

    public void Commit(ConsumeResult<string, string> result) =>
        CommittedOffsets.Add(result.TopicPartitionOffset);

    // The loop seeks back to a message whose write failed, so that the next read
    // returns it again. Re-queueing it here is what makes the fake behave the way
    // the real consumer would and lets a test assert on the redelivery.
    public void Seek(TopicPartitionOffset offset)
    {
        SeekedOffsets.Add(offset);

        var redelivered = new ConsumeResult<string, string>
        {
            Topic = offset.Topic,
            Partition = offset.Partition,
            Offset = offset.Offset,
            Message = LastConsumedMessage ?? new Message<string, string>(),
            IsPartitionEOF = false
        };

        _messages.Enqueue(redelivered);
    }

    // The message body of the most recent Consume, kept so Seek can re-queue it.
    public Message<string, string>? LastConsumedMessage { get; private set; }

    public void Subscribe(string topic) => SubscribedTopics.Add(topic);

    public void Subscribe(IEnumerable<string> topics) => SubscribedTopics.AddRange(topics);

    public void Close() => CloseCallCount++;

    public void Dispose() => GC.SuppressFinalize(this);

    // Everything below is interface surface the consume loop does not touch.

    public Handle Handle => throw new NotSupportedException();

    public string Name => nameof(FakeKafkaConsumer);

    public string MemberId => nameof(FakeKafkaConsumer);

    public List<TopicPartition> Assignment => new() { new TopicPartition(Topic, new Partition(0)) };

    public List<string> Subscription => SubscribedTopics;

    public IConsumerGroupMetadata ConsumerGroupMetadata => throw new NotSupportedException();

    public int AddBrokers(string brokers) => throw new NotSupportedException();

    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

    public ConsumeResult<string, string> Consume(int millisecondsTimeout) => throw new NotSupportedException();

    public ConsumeResult<string, string> Consume(TimeSpan timeout) => throw new NotSupportedException();

    public void Unsubscribe() => throw new NotSupportedException();

    public void Assign(TopicPartition partition) => throw new NotSupportedException();

    public void Assign(TopicPartitionOffset partition) => throw new NotSupportedException();

    public void Assign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();

    public void Assign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();

    public void IncrementalAssign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Unassign() => throw new NotSupportedException();

    public void StoreOffset(ConsumeResult<string, string> result) => throw new NotSupportedException();

    public void StoreOffset(TopicPartitionOffset offset) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Commit() => throw new NotSupportedException();

    public void Commit(IEnumerable<TopicPartitionOffset> offsets) => throw new NotSupportedException();

    public void Pause(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Resume(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(TimeSpan timeout) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) =>
        throw new NotSupportedException();

    public Offset Position(TopicPartition partition) => throw new NotSupportedException();

    public List<TopicPartitionOffset> OffsetsForTimes(
        IEnumerable<TopicPartitionTimestamp> timestampsToSearch,
        TimeSpan timeout) => throw new NotSupportedException();

    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) => throw new NotSupportedException();

    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) =>
        throw new NotSupportedException();
}
