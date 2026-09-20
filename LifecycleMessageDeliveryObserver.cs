using Common.Diagnostics;

namespace Common.Messaging;

public sealed class LifecycleMessageDeliveryObserver
{
    private readonly string applicationId;
    private readonly string instanceId;
    private readonly ILifecycleEventSink lifecycle;

    public LifecycleMessageDeliveryObserver(
        string applicationId,
        string instanceId,
        ILifecycleEventSink lifecycle)
    {
        if (string.IsNullOrWhiteSpace(applicationId)) throw new ArgumentException("Application id is required.", nameof(applicationId));
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance id is required.", nameof(instanceId));
        this.applicationId = applicationId.Trim();
        this.instanceId = instanceId.Trim();
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task ObserveAsync(
        MessageDeliveryTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        string correlationId = string.IsNullOrWhiteSpace(trace.CorrelationId)
            ? trace.MessageId.ToString("D")
            : trace.CorrelationId.Trim();

        LifecycleEventOutcome outcome = trace.State switch
        {
            MessageDeliveryState.Delivered or MessageDeliveryState.ProviderAccepted => LifecycleEventOutcome.Succeeded,
            MessageDeliveryState.Failed or MessageDeliveryState.DeadLetter => LifecycleEventOutcome.Failed,
            MessageDeliveryState.Unknown => LifecycleEventOutcome.Warning,
            _ => LifecycleEventOutcome.Started
        };

        try
        {
            await lifecycle.EmitAsync(
                LifecycleEvent.Create(
                    applicationId,
                    instanceId,
                    "MessagingDelivery",
                    trace.State.ToString(),
                    outcome,
                    correlationId,
                    operationId: trace.MessageId.ToString("D"),
                    code: trace.FailureCode,
                    properties: new Dictionary<string, string>
                    {
                        ["Provider"] = trace.Provider,
                        ["Attempt"] = trace.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
                    occurredAtUtc: trace.ObservedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Message delivery must not depend on telemetry availability.
        }
    }
}
