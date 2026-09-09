namespace Common.Messaging.Hosting;

using Common.Messaging.Channels.Slack;
using Common.Messaging.Channels.Sms;
using Common.Messaging.Channels.Smtp;
using Common.Messaging.Channels.Teams;
using Microsoft.Extensions.DependencyInjection;

public static class ChannelServiceCollectionExtensions
{
    public static IServiceCollection AddSlackMessagingChannel(this IServiceCollection services, SlackMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options);
        services.AddSingleton<IExternalMessageChannel>(provider =>
            new SlackMessageChannel(new HttpClient(), options, provider.GetService<IChannelSecretResolver>()));
        return services;
    }

    public static IServiceCollection AddTeamsMessagingChannel(this IServiceCollection services, TeamsMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options);
        services.AddSingleton<IExternalMessageChannel>(provider =>
            new TeamsMessageChannel(new HttpClient(), options, provider.GetService<IChannelSecretResolver>()));
        return services;
    }

    public static IServiceCollection AddSmtpMessagingChannel(this IServiceCollection services, SmtpMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options);
        services.AddSingleton<IExternalMessageChannel>(provider =>
            new SmtpMessageChannel(options, provider.GetService<IChannelSecretResolver>()));
        return services;
    }

    public static IServiceCollection AddSmsMessagingChannel(this IServiceCollection services, SmsMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options);
        services.AddSingleton<IExternalMessageChannel>(provider =>
            new SmsMessageChannel(new HttpClient(), options, provider.GetService<IChannelSecretResolver>()));
        return services;
    }
}
