namespace Common.Messaging.Hosting;

using Common.Diagnostics;
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
            services.AddSingleton<FileExternalDeliveryQueue>();
            services.AddSingleton<IExternalDeliveryQueue>(provider => provider.GetRequiredService<FileExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryQueueHealth>(provider => provider.GetRequiredService<FileExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryIdempotencyStore, FileExternalDeliveryIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<InMemoryExternalDeliveryQueue>();
            services.AddSingleton<IExternalDeliveryQueue>(provider => provider.GetRequiredService<InMemoryExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryQueueHealth>(provider => provider.GetRequiredService<InMemoryExternalDeliveryQueue>());
            services.AddSingleton<IExternalDeliveryIdempotencyStore, InMemoryExternalDeliveryIdempotencyStore>();
        }

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
}
