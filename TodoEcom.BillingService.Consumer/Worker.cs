using TodoEcom.Contracts.Events;
using TodoEcom.ServiceBus;

namespace TodoEcom.BillingService.Consumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceBus _serviceBus;
    private const string OrdersQueueName = "orders";

    public Worker(ILogger<Worker> logger, IServiceBus serviceBus)
    {
        _logger = logger;
        _serviceBus = serviceBus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            }

            var @event = await _serviceBus.Receive<OrderCreatedEvent>(OrdersQueueName, stoppingToken);

            Console.WriteLine($"Event received. OrderId: {@event.Data.OrderId} ");

            // some operations...

            await @event.CompleteAsync();
        }
    }
}