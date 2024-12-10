using TodoEcom.BillingService.Consumer;
using TodoEcom.ServiceBus;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IServiceBus, AzureServiceBus>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();