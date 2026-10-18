using Prometheus;

namespace JobService.Observability;

/// <summary>
/// Prometheus metrics for Job Service lifecycle transitions and completion messaging (Sprint 3).
/// </summary>
public static class JobMetrics
{
    /// <summary>
    /// Counts the total number of job status transitions (e.g. ASSIGNED -> IN_PROGRESS, IN_PROGRESS -> COMPLETED).
    /// </summary>
    public static readonly Counter StatusTransitionsTotal = Metrics.CreateCounter(
        "assms_job_status_transitions_total",
        "Total number of job status transitions recorded.",
        new CounterConfiguration
        {
            LabelNames = new[] { "old_status", "new_status" }
        });

    /// <summary>
    /// Counts total successfully completed jobs.
    /// </summary>
    public static readonly Counter JobCompletionsTotal = Metrics.CreateCounter(
        "assms_job_completions_total",
        "Total number of completed jobs.",
        new CounterConfiguration
        {
            LabelNames = new[] { "service_category", "priority" }
        });

    /// <summary>
    /// Counts Kafka event publication attempts for job status changes.
    /// </summary>
    public static readonly Counter StatusEventsPublishedTotal = Metrics.CreateCounter(
        "assms_job_status_events_published_total",
        "Total JobStatusChanged Kafka events published.",
        new CounterConfiguration
        {
            LabelNames = new[] { "event_type", "result" }
        });
}
