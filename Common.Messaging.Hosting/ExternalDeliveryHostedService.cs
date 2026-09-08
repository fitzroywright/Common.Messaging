namespace Common.Messaging.Hosting;

using Microsoft.Extensions.Hosting;

public sealed class ExternalDeliveryHostedService : BackgroundService
{
    private readonly ExternalDeliveryProcessor processor;

    public ExternalDeliveryHostedService(ExternalDeliveryProcessor processor)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return processor.RunAsync(stoppingToken);
    }
}
