using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace TodoEcom.ServiceBus;

public interface IServiceBus
{
    Task Publish<T>(T @event, string? queueName = null, CancellationToken cancellationToken = default);
    Task<ServiceBusReceivedMessageWrapper<T>> Receive<T>(string? queueName = null, CancellationToken cancellationToken = default);
}

public class ServiceBusReceivedMessageWrapper<T>
{
    private readonly ServiceBusReceivedMessage _receivedMessage;
    private readonly ServiceBusReceiver _receiver;
    public T Data { get; }

    internal ServiceBusReceivedMessageWrapper(ServiceBusReceivedMessage receivedMessage, ServiceBusReceiver receiver, T payload)
    {
        _receivedMessage = receivedMessage;
        _receiver = receiver;
        Data = payload;
    }

    public async Task CompleteAsync()
    {
        await _receiver.CompleteMessageAsync(_receivedMessage);
    }
}

public class AzureServiceBus : IServiceBus
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly BlobContainerClient? _blobContainerClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senderPool;
    private readonly ConcurrentDictionary<string, ServiceBusReceiver> _receiverPool;
    private readonly int _maxEventPayloadSizeLimitInKB;
    private readonly int _claimCheckTokenExpirationInHours;
    private const string IsClaimCheckProperty = "IsClaimCheck";
    private const string ClaimCheckBlobUriProperty = "ClaimCheckBlobUri";
    private readonly bool _enableClaimCheck;
    private readonly string? _defaultQueueName;

    public AzureServiceBus(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        var serviceBusConnectionString = configuration.GetValue<string>("ServiceBus:ConnectionString") ?? throw new KeyNotFoundException("ServiceBus:ConnectionString is not found.");
        _defaultQueueName = configuration.GetValue<string>("ServiceBus:DefaultQueueName");
        _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);

        _httpClientFactory = httpClientFactory;

        _senderPool = new ConcurrentDictionary<string, ServiceBusSender>();
        _receiverPool = new ConcurrentDictionary<string, ServiceBusReceiver>();


        _enableClaimCheck = configuration.GetValue<bool>("ServiceBus:ClaimCheck:EnableClaimCheck");

        if(_enableClaimCheck)
        {
            var blobStorageConnectionString = configuration.GetValue<string>("ServiceBus:ClaimCheck:BlobStorage:ConnectionString") ?? throw new KeyNotFoundException("ServiceBus:BlobStorage:ConnectionString is not found.");

            var claimCheckContainerName = configuration.GetValue<string>("ServiceBus:ClaimCheck:BlobStorage:ClaimCheckContainerName") ?? throw new KeyNotFoundException("ServiceBus:BlobStorage:ClaimCheckContainerName is not found.");

            _maxEventPayloadSizeLimitInKB = configuration.GetValue<int?>("ServiceBus:ClaimCheck:MaxEventPayloadSizeLimitInKB") ?? throw new KeyNotFoundException("ServiceBus:MaxEventPayloadSizeLimitInKB is not found.");

            _claimCheckTokenExpirationInHours = configuration.GetValue<int?>("ServiceBus:ClaimCheck:BlobStorage:ClaimCheckTokenExpirationInHours") ?? throw new KeyNotFoundException("ServiceBus:BlobStorage:ClaimCheckTokenExpirationInHours is not found.");

            _blobContainerClient = new BlobContainerClient(blobStorageConnectionString, claimCheckContainerName);
        }
    }

    public async Task Publish<T>(T @event, string? queueName = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrEmpty(queueName))
        {
            queueName = _defaultQueueName!;
        }

        var sender = _senderPool.GetOrAdd(queueName, _serviceBusClient.CreateSender);

        var serializedEvent = JsonSerializer.SerializeToUtf8Bytes(@event);

        ServiceBusMessage message;
        if (_enableClaimCheck && IsEventPayloadSizeExceedsLimit(serializedEvent))
        {
            var blobUri = await UploadPayloadToBlobAsync(serializedEvent, queueName, @event!.GetType().Name);

            message = new ServiceBusMessage
            {
                ApplicationProperties =
                {
                    [IsClaimCheckProperty] = true,
                    [ClaimCheckBlobUriProperty] = blobUri
                }
            };
        }
        else
        {
            message = new ServiceBusMessage(serializedEvent);
        }

        await sender.SendMessageAsync(message, cancellationToken);
    }

    private bool IsEventPayloadSizeExceedsLimit(byte[] serializedEvent)
    {
        var eventSize = serializedEvent.Length;
        Console.WriteLine(eventSize);
        return eventSize > _maxEventPayloadSizeLimitInKB * 1024;
    }

    private async Task<string> UploadPayloadToBlobAsync(byte[] serializedEvent, string queueName, string eventType)
    {
        var blobName = $"{queueName}/{eventType}/{Guid.NewGuid()}.json";

        await _blobContainerClient!.CreateIfNotExistsAsync();
        var blobClient = _blobContainerClient!.GetBlobClient(blobName);

        using var stream = new MemoryStream(serializedEvent);
        await blobClient.UploadAsync(stream, overwrite: true);

        var sasUri = blobClient.GenerateSasUri(Azure.Storage.Sas.BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(_claimCheckTokenExpirationInHours));

        return sasUri.ToString();
    }

    public async Task<ServiceBusReceivedMessageWrapper<T>> Receive<T>(string? queueName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(queueName))
        {
            queueName = _defaultQueueName!;
        }

        var receiver = _receiverPool.GetOrAdd(queueName, _serviceBusClient.CreateReceiver);

        var message = await receiver.ReceiveMessageAsync(cancellationToken: cancellationToken);

        ServiceBusReceivedMessageWrapper<T> wrappedMessage;

        if (message.ApplicationProperties.TryGetValue(IsClaimCheckProperty, out var isClaimCheck) && (bool)isClaimCheck)
        {
            var blobUri = message.ApplicationProperties[ClaimCheckBlobUriProperty] as string
                  ?? throw new InvalidOperationException($"The claim-check value is not found. Message CorrelationId: {message.CorrelationId}");

            byte[] payload = await DownloadPayloadFromBlobAsync((string)blobUri);

            var actualEvent = JsonSerializer.Deserialize<T>(payload);
            
            wrappedMessage = new ServiceBusReceivedMessageWrapper<T>(message, receiver, actualEvent!);
        }
        else
        {
            wrappedMessage = new ServiceBusReceivedMessageWrapper<T>(message, receiver, message.Body.ToObjectFromJson<T>()!);
        }

        return wrappedMessage;
    }

    private async Task<byte[]> DownloadPayloadFromBlobAsync(string blobUri)
    {
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.GetAsync(blobUri);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsByteArrayAsync();
        }

        throw new Exception($"Failed to download the claim-check payload from {blobUri}. Status Code: {response.StatusCode}");
    }
}