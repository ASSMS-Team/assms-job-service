using JobService.Observability;
using JobService.Repositories;
using Prometheus;

namespace JobService.Messaging.Producers;

/// <summary>
/// Background service that continuously checks for and publishes pending JobStatusChanged
/// outbox events to Kafka, guaranteeing durable delivery even across broker outages (S4-KAFKA-01).
/// </summary>
public sealed class JobStatusOutboxPublisher : BackgroundService
{
    private static readonly Gauge OutboxPendingGauge = Metrics.CreateGauge(
        "assms_job_outbox_pending_count", "Number of unsent status outbox events waiting in database queue");

    private static readonly Counter OutboxPublishedCounter = Metrics.CreateCounter(
        "assms_job_outbox_published_total", "Total outbox status events published to Kafka", "topic");

    private static readonly Counter OutboxFailedCounter = Metrics.CreateCounter(
        "assms_job_outbox_failed_total", "Total outbox status publication failures", "topic", "error_type");

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<JobStatusOutboxPublisher> _logger;

    public JobStatusOutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IEventPublisher publisher,
        ILogger<JobStatusOutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Unexpected error in JobStatusOutboxPublisher iteration.");
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task PublishPendingAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var pendingEvents = await repository.GetPendingJobStatusOutboxEventsAsync(20, cancellationToken);
        OutboxPendingGauge.Set(pendingEvents.Count);

        foreach (var pendingEvent in pendingEvents)
        {
            try
            {
                await _publisher.PublishRawAsync(pendingEvent.Topic, pendingEvent.JobId, pendingEvent.Payload, cancellationToken);
                await repository.MarkJobStatusOutboxPublishedAsync(pendingEvent.Id, DateTime.UtcNow, cancellationToken);
                OutboxPublishedCounter.WithLabels(pendingEvent.Topic).Inc();
                JobMetrics.StatusEventsPublishedTotal.WithLabels(pendingEvent.EventType, "success").Inc();
                _logger.LogInformation("Published queued JobStatusChanged outbox event {EventId} for job {JobId}.", pendingEvent.Id, pendingEvent.JobId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await repository.MarkJobStatusOutboxFailedAsync(pendingEvent.Id, exception.Message, cancellationToken);
                OutboxFailedCounter.WithLabels(pendingEvent.Topic, exception.GetType().Name).Inc();
                JobMetrics.StatusEventsPublishedTotal.WithLabels(pendingEvent.EventType, "failed").Inc();
                _logger.LogWarning(exception, "JobStatusChanged outbox event {EventId} for job {JobId} remains pending for retry.", pendingEvent.Id, pendingEvent.JobId);
            }
        }
    }
}
