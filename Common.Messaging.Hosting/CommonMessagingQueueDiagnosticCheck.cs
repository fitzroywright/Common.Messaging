namespace Common.Messaging.Hosting;

using Common.Diagnostics;

public sealed class CommonMessagingQueueDiagnosticCheck(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : IDiagnosticCheck
{
    public string Name => "Common.Messaging.Queue";

    public async Task<DiagnosticResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            ExternalDeliveryQueueHealth health =
                await queueHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

            DiagnosticStatus status;
            string message;

            if (!health.IsAvailable)
            {
                status = DiagnosticStatus.Unhealthy;
                message = "Durable messaging queue is unavailable.";
            }
            else if (health.ExpiredLeases >= options.ExpiredLeaseCriticalThreshold ||
                     health.Pending >= options.PendingCriticalThreshold ||
                     health.DeadLettered >= options.DeadLetterCriticalThreshold ||
                     (health.OldestPendingAge is { } oldestCritical &&
                      oldestCritical >= options.OldestPendingCriticalAge))
            {
                status = DiagnosticStatus.Unhealthy;
                message = $"Messaging queue is critical: pending={health.Pending}, dead-letter={health.DeadLettered}, retrying={health.Retrying}, expired-leases={health.ExpiredLeases}.";
            }
            else if (health.Pending >= options.PendingWarningThreshold ||
                     health.DeadLettered >= options.DeadLetterWarningThreshold ||
                     health.Retrying >= options.RetryWarningThreshold ||
                     (health.OldestPendingAge is { } oldestWarning &&
                      oldestWarning >= options.OldestPendingWarningAge))
            {
                status = DiagnosticStatus.Warning;
                message = $"Messaging queue requires attention: pending={health.Pending}, dead-letter={health.DeadLettered}, retrying={health.Retrying}.";
            }
            else
            {
                status = DiagnosticStatus.Healthy;
                message = $"Messaging queue is healthy: pending={health.Pending}, dead-letter={health.DeadLettered}, retrying={health.Retrying}.";
            }

            return new DiagnosticResult(
                Name,
                status,
                message,
                DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                Name,
                DiagnosticStatus.Unknown,
                "Messaging queue health could not be established.",
                DateTimeOffset.UtcNow - started,
                ex);
        }
    }
}
