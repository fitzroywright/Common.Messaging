namespace Common.Messaging.Hosting;

using Common.Diagnostics;
using Common.Secrets;
using Microsoft.Extensions.Logging;

public sealed class CommonSecretsChannelSecretResolver : IChannelSecretResolver
{
    private readonly ISecretProvider secrets;

    public CommonSecretsChannelSecretResolver(ISecretProvider secrets)
    {
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        return secrets.GetAsync(secretName, cancellationToken);
    }
}

public sealed class CommonMessagingDiagnosticCheck : IDiagnosticCheck
{
    private readonly IExternalDeliveryQueueHealth queueHealth;

    public CommonMessagingDiagnosticCheck(IExternalDeliveryQueueHealth queueHealth)
    {
        this.queueHealth = queueHealth ?? throw new ArgumentNullException(nameof(queueHealth));
    }

    public string Name => "Common.Messaging";

    public async Task<DiagnosticResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        ExternalDeliveryQueueHealth health = await queueHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticStatus status = !health.IsAvailable
            ? DiagnosticStatus.Unhealthy
            : health.DeadLettered > 0
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;
        string message = health.IsAvailable
            ? $"Queue available. Pending={health.Pending}; DeadLettered={health.DeadLettered}."
            : "Messaging queue is unavailable.";
        return new DiagnosticResult(Name, status, message, DateTimeOffset.UtcNow - started);
    }
}

public sealed class MessagingDeliveryTelemetrySink : IExternalDeliveryFailureSink, IExternalDeliverySuccessSink
{
    private readonly ILogger<MessagingDeliveryTelemetrySink> logger;

    public MessagingDeliveryTelemetrySink(ILogger<MessagingDeliveryTelemetrySink> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task RecordAsync(ExternalDeliveryFailure failure, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "MSG201 Delivery failed. NotificationId={NotificationId} Recipient={Recipient} Channel={Channel} ErrorCode={ErrorCode} Attempt={Attempt} CorrelationId={CorrelationId}",
            failure.NotificationId,
            failure.RecipientUserId,
            failure.Channel,
            failure.ErrorCode,
            failure.Attempt,
            failure.CorrelationId);
        return Task.CompletedTask;
    }

    public Task RecordAsync(ExternalDeliverySuccess success, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "MSG101 Delivery succeeded. NotificationId={NotificationId} Recipient={Recipient} Channel={Channel} CorrelationId={CorrelationId}",
            success.NotificationId,
            success.RecipientUserId,
            success.Channel,
            success.CorrelationId);
        return Task.CompletedTask;
    }
}
