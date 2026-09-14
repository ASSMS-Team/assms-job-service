using JobService.Messaging.Consumers;
using JobService.Messaging.Contracts;

namespace JobService.Tests;

// Pins this service's redefinition of the JobAssigned payload against a message
// in the exact shape the Dispatch Service publishes.
//
// Each of the three consuming services declares its own copy of the payload, so
// nothing at compile time catches a field that has drifted - a renamed field
// simply deserializes as null forever, and the job silently stops being updated.
// These tests are what stands in for that missing check.
public class JobAssignedContractTests
{
    // Copied from what DispatchService.Repositories.AutomaticAssignmentRepository
    // serializes: the six-field payload, camelCase, inside the shared six-field
    // envelope, with UTC timestamps.
    private const string PublishedMessage = """
        {
          "eventId": "7b41e9c6-0d38-4a52-9f17-3c8b6e2d5a04",
          "eventType": "JobAssigned",
          "eventVersion": 1,
          "occurredAt": "2026-09-14T09:15:02.446Z",
          "producer": "dispatch-service",
          "payload": {
            "assignmentId": "e2d7b415-9a63-4c08-b1f5-7d4e2a9c6301",
            "jobId": "9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77",
            "jobReference": "JOB-7K2M9X",
            "technicianId": "5c8a3f71-4b29-4e6d-8a03-9f1b7c2e4d58",
            "technicianReference": "TECH-0001",
            "assignedAt": "2026-09-14T09:15:02.446Z"
          }
        }
        """;

    [Fact]
    public void Topic_AndConsumerGroup_MatchTheContractDocument()
    {
        // The group follows the contract's assms-<consuming-service>-<topic>
        // convention. It must never equal Reporting's group on this topic: a
        // shared group would make Kafka hand each event to only one of the two,
        // and both services need every event.
        Assert.Equal("job-assigned", JobAssignedPayload.Topic);
        Assert.Equal("assms-job-job-assigned", JobAssignedPayload.ConsumerGroup);
        Assert.NotEqual("assms-reporting-job-assigned", JobAssignedPayload.ConsumerGroup);
    }

    [Fact]
    public void Deserialize_ReadsAllSixPayloadFieldsDispatchPublishes()
    {
        // Act
        var envelope = JobAssignedConsumer.Deserialize(PublishedMessage);

        // Assert - all six, by name. The reviewer's count: six principal payload
        // fields, not eight.
        var payload = envelope!.Payload;

        Assert.Equal("e2d7b415-9a63-4c08-b1f5-7d4e2a9c6301", payload.AssignmentId);
        Assert.Equal("9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77", payload.JobId);
        Assert.Equal("JOB-7K2M9X", payload.JobReference);
        Assert.Equal("5c8a3f71-4b29-4e6d-8a03-9f1b7c2e4d58", payload.TechnicianId);
        Assert.Equal("TECH-0001", payload.TechnicianReference);
        Assert.Equal(new DateTime(2026, 9, 14, 9, 15, 2, 446, DateTimeKind.Utc), payload.AssignedAt);
    }

    [Fact]
    public void PayloadType_DeclaresExactlySixFields()
    {
        // Guards the count directly. A field added here without the contract
        // document and the other two services being updated is the drift this
        // whole file exists to catch, and it would otherwise pass every other
        // test in the suite.
        var declared = typeof(JobAssignedPayload)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AssignedAt",
                "AssignmentId",
                "JobId",
                "JobReference",
                "TechnicianId",
                "TechnicianReference"
            },
            declared);
    }

    [Fact]
    public void Deserialize_ReadsTheEnvelopeAsWellAsThePayload()
    {
        // Act
        var envelope = JobAssignedConsumer.Deserialize(PublishedMessage);

        // Assert - the five non-payload fields are part of the contract too, and
        // eventVersion is what a future consumer would branch on.
        Assert.Equal("7b41e9c6-0d38-4a52-9f17-3c8b6e2d5a04", envelope!.EventId);
        Assert.Equal("JobAssigned", envelope.EventType);
        Assert.Equal(JobAssignedPayload.EventType, envelope.EventType);
        Assert.Equal(1, envelope.EventVersion);
        Assert.Equal(JobAssignedPayload.EventVersion, envelope.EventVersion);
        Assert.Equal("dispatch-service", envelope.Producer);
        Assert.Equal(new DateTime(2026, 9, 14, 9, 15, 2, 446, DateTimeKind.Utc), envelope.OccurredAt);
    }

    [Fact]
    public void Deserialize_ReadsAssignedAtAsUtc()
    {
        // The stale-event guard compares this against the stored timestamp, so a
        // value read as local time would make the comparison depend on the
        // server's timezone.
        var envelope = JobAssignedConsumer.Deserialize(PublishedMessage);

        Assert.Equal(DateTimeKind.Utc, envelope!.Payload.AssignedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, envelope.OccurredAt.Kind);
    }

    [Fact]
    public void Deserialize_ToleratesAFieldAddedAfterThisServiceWasWritten()
    {
        // The contract's versioning rule: adding an optional field does not
        // change eventVersion, so consumers must ignore fields they do not
        // recognise rather than failing on them.
        const string withExtraField = """
            {
              "eventId": "7b41e9c6-0d38-4a52-9f17-3c8b6e2d5a04",
              "eventType": "JobAssigned",
              "eventVersion": 1,
              "occurredAt": "2026-09-14T09:15:02.446Z",
              "producer": "dispatch-service",
              "payload": {
                "assignmentId": "e2d7b415-9a63-4c08-b1f5-7d4e2a9c6301",
                "jobId": "9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77",
                "jobReference": "JOB-7K2M9X",
                "technicianId": "5c8a3f71-4b29-4e6d-8a03-9f1b7c2e4d58",
                "technicianReference": "TECH-0001",
                "assignedAt": "2026-09-14T09:15:02.446Z",
                "dispatcherNote": "added by a later story"
              }
            }
            """;

        var envelope = JobAssignedConsumer.Deserialize(withExtraField);

        Assert.Equal("9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77", envelope!.Payload.JobId);
    }

    [Fact]
    public void Deserialize_OfMalformedJson_Throws()
    {
        // The loop distinguishes a throw here from a failed write: this one is
        // committed past, that one is read again. The distinction only holds if
        // malformed input actually throws.
        Assert.Throws<System.Text.Json.JsonException>(() => JobAssignedConsumer.Deserialize("{ not json"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"eventId":"a","eventType":"JobAssigned"}""")]
    public void IsUsable_OfAnEventWithNoPayload_IsFalse(string body)
    {
        Assert.False(JobAssignedConsumer.IsUsable(JobAssignedConsumer.Deserialize(body)));
    }

    [Fact]
    public void IsUsable_OfTheFullPublishedMessage_IsTrue()
    {
        Assert.True(JobAssignedConsumer.IsUsable(JobAssignedConsumer.Deserialize(PublishedMessage)));
    }
}
