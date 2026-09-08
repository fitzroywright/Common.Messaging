namespace Common.Messaging.Hosting;

using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCommonMessagingQueuedDelivery(
        this IServiceCollection services,
        ExternalDeliveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        ExternalDeliveryOptions resolvedOptions = options ?? new ExternalDeliveryOptions();
        services.AddSingleton(resolvedOptions);
        services.AddSingleton<IExternalDeliveryQueue, InMemoryExternalDeliveryQueue>();
        services.AddSingleton<IExternalDeliveryDispatcher>(serviceProvider =>
            new ExternalDeliveryDispatcher(
                serviceProvider.GetServices<IExternalMessageChannel>(),
                serviceProvider.GetRequiredService<ExternalDeliveryOptions>(),
                serviceProvider.GetService<IExternalDeliveryFailureSink>()));
        services.AddSingleton<ExternalDeliveryProcessor>();
        services.AddHostedService<ExternalDeliveryHostedService>();

        services.AddTransient<IMessageService>(serviceProvider =>
            new QueuedMessageService(
                serviceProvider.GetRequiredService<IMessageRecipientDirectory>(),
                serviceProvider.GetRequiredService<IMessageStore>(),
                serviceProvider.GetRequiredService<IExternalDeliveryQueue>(),
                serviceProvider.GetService<IMessageSignalSender>()));

        return services;
    }
}
