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

public sealed class CommonMessagingDiagnosticCheck : ILeveledDiagnosticCheck
{
    private readonly IExternalDeliveryQueueHealth queueHealth;
    private readonly ExternalDeliveryOptions options;

    public CommonMessagingDiagnosticCheck(IExternalDeliveryQueueHealth queueHealth, ExternalDeliveryOptions options)
    {
        this.queueHealth = queueHealth ?? throw new ArgumentNullException(nameof(queueHealth));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => "Common.Messaging";

    public DiagnosticLevel Level => DiagnosticLevel.Level4;

    public async Task<DiagnosticResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        ExternalDeliveryQueueHealth health = await queueHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        DiagnosticStatus status;
        if (!health.IsAvailable)
        {
            status = DiagnosticStatus.Unhealthy;
        }
        else if (health.DeadLettered >= options.DeadLetterCriticalThreshold ||
                 health.Pending >= options.PendingCriticalThreshold ||
                 health.ExpiredLeases >= options.ExpiredLeaseCriticalThreshold ||
                 health.OldestPendingAge >= options.OldestPendingCriticalAge)
        {
            status = DiagnosticStatus.Unhealthy;
        }
        else if (health.DeadLettered >= options.DeadLetterWarningThreshold ||
                 health.Pending >= options.PendingWarningThreshold ||
                 health.Retrying >= options.RetryWarningThreshold ||
                 health.OldestPendingAge >= options.OldestPendingWarningAge)
        {
            status = DiagnosticStatus.Warning;
        }
        else
        {
            status = DiagnosticStatus.Healthy;
        }

        string oldest = health.OldestPendingAge.HasValue
            ? FormatAge(health.OldestPendingAge.Value)
            : "none";
        string queueName = string.IsNullOrWhiteSpace(health.QueueName) ? "default" : health.QueueName;
        string message = health.IsAvailable
            ? $"Queue={queueName}; Pending={health.Pending}; Ready={health.Ready}; Leased={health.Leased}; Retrying={health.Retrying}; ExpiredLeases={health.ExpiredLeases}; DeadLettered={health.DeadLettered}; OldestPending={oldest}."
            : $"Queue={queueName}; messaging queue metrics are unavailable.";

        return new DiagnosticResult(Name, status, message, DateTimeOffset.UtcNow - started);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours >= 1) return $"{age.TotalHours:F1}h";
        if (age.TotalMinutes >= 1) return $"{age.TotalMinutes:F1}m";
        return $"{Math.Max(0, age.TotalSeconds):F0}s";
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
