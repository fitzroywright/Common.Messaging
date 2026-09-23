namespace Common.Messaging.Hosting;

using Common.Diagnostics;

public abstract class CommonMessagingQueueDiagnosticLevelTest : IDiagnosticLevelLocalTest
{
    protected readonly IExternalDeliveryQueueHealth QueueHealth;
    protected readonly ExternalDeliveryOptions Options;

    protected CommonMessagingQueueDiagnosticLevelTest(IExternalDeliveryQueueHealth queueHealth, ExternalDeliveryOptions options)
    {
        QueueHealth = queueHealth ?? throw new ArgumentNullException(nameof(queueHealth));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public abstract string TestId { get; }
    public abstract string Name { get; }
    public string Owner => "Common.Messaging";
    public abstract EngineeringDiagnosticLevel Level { get; }
    public bool IsDestructive => false;
    public abstract Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken);

    protected Task<ExternalDeliveryQueueHealth> HealthAsync(CancellationToken cancellationToken)
        => QueueHealth.CheckHealthAsync(cancellationToken);
}

public sealed class MessagingQueueAvailableDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L5.QUEUE.AVAILABLE";
    public override string Name => "Outbound queue health available";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        return health.IsAvailable
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Outbound messaging queue is available.", $"Queue={health.QueueName ?? "default"}")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Outbound messaging queue is unavailable.", Redact(health.Message));
    }

    private static string Redact(string? value) => string.IsNullOrWhiteSpace(value) ? "No provider detail supplied." : "Provider reported unavailable.";
}

public sealed class MessagingQueueBacklogDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L4.QUEUE.BACKLOG";
    public override string Name => "Outbound queue backlog within threshold";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        string evidence = $"Pending={health.Pending}; Warning={Options.PendingWarningThreshold}; Critical={Options.PendingCriticalThreshold}";
        if (health.Pending >= Options.PendingCriticalThreshold)
            return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Outbound queue backlog is at or above the critical threshold.", evidence);
        if (health.Pending >= Options.PendingWarningThreshold)
            return EngineeringDiagnosticPolicy.Warning(TestId, Name, "Outbound queue backlog is at or above the warning threshold.", evidence);
        return EngineeringDiagnosticPolicy.Passed(TestId, Name, "Outbound queue backlog is within threshold.", evidence);
    }
}

public sealed class MessagingDeadLetterDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L4.QUEUE.DEADLETTER";
    public override string Name => "Dead-letter volume within threshold";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        string evidence = $"DeadLettered={health.DeadLettered}; Warning={Options.DeadLetterWarningThreshold}; Critical={Options.DeadLetterCriticalThreshold}";
        if (health.DeadLettered >= Options.DeadLetterCriticalThreshold)
            return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Dead-letter volume is at or above the critical threshold.", evidence);
        if (health.DeadLettered >= Options.DeadLetterWarningThreshold)
            return EngineeringDiagnosticPolicy.Warning(TestId, Name, "Dead-letter volume is at or above the warning threshold.", evidence);
        return EngineeringDiagnosticPolicy.Passed(TestId, Name, "Dead-letter volume is within threshold.", evidence);
    }
}

public sealed class MessagingRetryDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L4.QUEUE.RETRIES";
    public override string Name => "Retry volume within threshold";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        string evidence = $"Retrying={health.Retrying}; Warning={Options.RetryWarningThreshold}";
        return health.Retrying >= Options.RetryWarningThreshold
            ? EngineeringDiagnosticPolicy.Warning(TestId, Name, "Outbound retry volume is elevated.", evidence)
            : EngineeringDiagnosticPolicy.Passed(TestId, Name, "Outbound retry volume is within threshold.", evidence);
    }
}

public sealed class MessagingExpiredLeaseDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L4.QUEUE.EXPIRED_LEASES";
    public override string Name => "Expired delivery leases absent";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        string evidence = $"ExpiredLeases={health.ExpiredLeases}; Critical={Options.ExpiredLeaseCriticalThreshold}";
        return health.ExpiredLeases >= Options.ExpiredLeaseCriticalThreshold
            ? EngineeringDiagnosticPolicy.Failed(TestId, Name, "Expired delivery leases reached the critical threshold.", evidence)
            : EngineeringDiagnosticPolicy.Passed(TestId, Name, "Expired delivery leases are within threshold.", evidence);
    }
}

public sealed class MessagingOldestPendingDiagnosticLevelTest(
    IExternalDeliveryQueueHealth queueHealth,
    ExternalDeliveryOptions options) : CommonMessagingQueueDiagnosticLevelTest(queueHealth, options)
{
    public override string TestId => "COMMON.MESSAGING.L4.QUEUE.OLDEST_PENDING";
    public override string Name => "Oldest pending message age within threshold";
    public override EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public override async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        ExternalDeliveryQueueHealth health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan? age = health.OldestPendingAge;
        string evidence = $"OldestPending={age?.ToString() ?? "none"}; Warning={Options.OldestPendingWarningAge}; Critical={Options.OldestPendingCriticalAge}";
        if (age >= Options.OldestPendingCriticalAge)
            return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Oldest pending message exceeded the critical age.", evidence);
        if (age >= Options.OldestPendingWarningAge)
            return EngineeringDiagnosticPolicy.Warning(TestId, Name, "Oldest pending message exceeded the warning age.", evidence);
        return EngineeringDiagnosticPolicy.Passed(TestId, Name, "Oldest pending message age is within threshold.", evidence);
    }
}

public sealed class MessagingDeliveryOptionsDiagnosticLevelTest(ExternalDeliveryOptions options) : IDiagnosticLevelLocalTest
{
    private readonly ExternalDeliveryOptions options = options ?? throw new ArgumentNullException(nameof(options));
    public string TestId => "COMMON.MESSAGING.L5.CONFIG.DELIVERY_OPTIONS";
    public string Name => "Delivery options internally valid";
    public string Owner => "Common.Messaging";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        List<string> invalid = [];
        if (options.MaxConcurrency <= 0) invalid.Add(nameof(options.MaxConcurrency));
        if (options.QueueCapacity <= 0) invalid.Add(nameof(options.QueueCapacity));
        if (options.MaxAttempts <= 0) invalid.Add(nameof(options.MaxAttempts));
        if (options.InitialRetryDelay < TimeSpan.Zero) invalid.Add(nameof(options.InitialRetryDelay));
        if (options.MaxRetryDelay < options.InitialRetryDelay) invalid.Add(nameof(options.MaxRetryDelay));
        if (options.LeaseDuration <= TimeSpan.Zero) invalid.Add(nameof(options.LeaseDuration));

        return Task.FromResult(invalid.Count == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Messaging delivery options are internally valid.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Messaging delivery options contain invalid values.", $"Invalid={string.Join(",", invalid)}"));
    }
}

public sealed class MessagingChannelsRegisteredDiagnosticLevelTest(IEnumerable<IExternalMessageChannel> channels) : IDiagnosticLevelLocalTest
{
    private readonly IExternalMessageChannel[] channels = (channels ?? throw new ArgumentNullException(nameof(channels))).ToArray();
    public string TestId => "COMMON.MESSAGING.L5.CHANNELS.REGISTERED";
    public string Name => "External messaging channels registered";
    public string Owner => "Common.Messaging";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        string[] names = channels.Select(x => x.Channel.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        return Task.FromResult(names.Length == 0
            ? EngineeringDiagnosticPolicy.Warning(TestId, Name, "No external messaging channels are registered.")
            : EngineeringDiagnosticPolicy.Passed(TestId, Name, $"{names.Length} external messaging channel(s) are registered.", string.Join(",", names)));
    }
}
