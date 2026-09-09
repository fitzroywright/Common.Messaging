namespace Common.Messaging.Hosting;

using Common.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCommonMessagingQueuedDelivery(
        this IServiceCollection services,
        ExternalDeliveryOptions? options = null,
        bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        ExternalDeliveryOptions resolvedOptions = options ?? new ExternalDeliveryOptions();
        services.AddSingleton(resolvedOptions);

        if (durable)
        {
            services.AddSingleton(provider =>
                new ConfiguredExternalDeliveryStore(
                    provider.GetService<IConfiguration>(),
                    provider.GetRequiredService<ExternalDeliveryOptions>()));
            services.AddSingleton<IExternalDeliveryQueue>(provider => provider.GetRequiredService<ConfiguredExternalDeliveryStore>());
            services.AddSingleton<IExternalDeliveryQueueHealth>(provider => provider.GetRequiredService<ConfiguredExternalDeliveryStore>());
            services.AddSingleton<IExternalDeliveryDeadLetterStore>(provider => provider.GetRequiredService<ConfiguredExternalDeliveryStore>());
            services.AddSingleton<IExternalDeliveryIdempotencyStore>(provider => provider.GetRequiredService<ConfiguredExternalDeliveryStore>());
        }
        else
        {
            services.AddSingleton<InMemoryExternalDeliveryQueue>();
            services.AddSingleton<IExternalDeliveryQueue>(provider => provider.GetRequiredService<InMemoryExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryQueueHealth>(provider => provider.GetRequiredService<InMemoryExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryIdempotencyStore, InMemoryExternalDeliveryIdempotencyStore>();
        }

        AddQueuedDeliveryCore(services);
        return services;
    }

    public static IServiceCollection AddCommonMessagingPostgreSqlDelivery(
        this IServiceCollection services,
        string connectionString,
        string queueName,
        ExternalDeliveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        ExternalDeliveryOptions resolvedOptions = options ?? new ExternalDeliveryOptions();
        services.AddSingleton(resolvedOptions);
        services.AddSingleton(provider =>
            new PostgreSqlExternalDeliveryStore(
                connectionString,
                provider.GetRequiredService<ExternalDeliveryOptions>(),
                queueName));
        services.AddSingleton<IExternalDeliveryQueue>(provider => provider.GetRequiredService<PostgreSqlExternalDeliveryStore>());
        services.AddSingleton<IExternalDeliveryQueueHealth>(provider => provider.GetRequiredService<PostgreSqlExternalDeliveryStore>());
        services.AddSingleton<IExternalDeliveryDeadLetterStore>(provider => provider.GetRequiredService<PostgreSqlExternalDeliveryStore>());
        services.AddSingleton<IExternalDeliveryIdempotencyStore>(provider => provider.GetRequiredService<PostgreSqlExternalDeliveryStore>());

        AddQueuedDeliveryCore(services);
        return services;
    }

    public static IServiceCollection AddCommonMessagingDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDiagnosticCheck, CommonMessagingDiagnosticCheck>();
        return services;
    }

    public static IServiceCollection AddCommonMessagingSecrets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IChannelSecretResolver, CommonSecretsChannelSecretResolver>();
        return services;
    }

    private static void AddQueuedDeliveryCore(IServiceCollection services)
    {
        services.TryAddSingleton<MessagingDeliveryTelemetrySink>();
        services.TryAddSingleton<IExternalDeliveryFailureSink>(provider => provider.GetRequiredService<MessagingDeliveryTelemetrySink>());
        services.TryAddSingleton<IExternalDeliverySuccessSink>(provider => provider.GetRequiredService<MessagingDeliveryTelemetrySink>());

        services.AddSingleton<IExternalDeliveryDispatcher>(serviceProvider =>
            new ExternalDeliveryDispatcher(
                serviceProvider.GetServices<IExternalMessageChannel>(),
                serviceProvider.GetRequiredService<ExternalDeliveryOptions>(),
                serviceProvider.GetService<IExternalDeliveryFailureSink>(),
                serviceProvider.GetService<IExternalDeliverySuccessSink>(),
                serviceProvider.GetRequiredService<IExternalDeliveryIdempotencyStore>()));
        services.AddSingleton<ExternalDeliveryProcessor>(serviceProvider =>
            new ExternalDeliveryProcessor(
                serviceProvider.GetRequiredService<IExternalDeliveryQueue>(),
                serviceProvider.GetRequiredService<IExternalDeliveryDispatcher>(),
                serviceProvider.GetRequiredService<ExternalDeliveryOptions>(),
                serviceProvider.GetService<IExternalDeliveryFailureSink>()));
        services.AddHostedService<ExternalDeliveryHostedService>();

        services.AddTransient<IMessageService>(serviceProvider =>
            new QueuedMessageService(
                serviceProvider.GetRequiredService<IMessageRecipientDirectory>(),
                serviceProvider.GetRequiredService<IMessageStore>(),
                serviceProvider.GetRequiredService<IExternalDeliveryQueue>(),
                serviceProvider.GetService<IMessageSignalSender>()));
    }
}
