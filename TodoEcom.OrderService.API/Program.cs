using TodoEcom.Contracts.Events;
using TodoEcom.ServiceBus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IServiceBus, AzureServiceBus>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/orders", async (IServiceBus serviceBus) =>
{
    // Perform order operations...

    var orderCreatedEvent = new OrderCreatedEvent
    {
        OrderId = 1,
        CustomerId = 123,
        PaymentMethod = "CreditCard",
        CreatedAt = DateTime.UtcNow,
        Products = Enumerable.Range(1,20).Select(i => new ProductDTO
        {
            ProductId = i,
            Name = $"My product {i}",
            Quantity = 1,
            Price = i,
        }).ToList()
    };

    await serviceBus.Publish(orderCreatedEvent);

})
.WithName("CreateAnOrder");

app.Run();