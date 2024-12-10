namespace TodoEcom.Contracts.Events;

public class OrderCreatedEvent
{
    public int OrderId { get; set; }
    public int CustomerId { get; set; }
    public required string PaymentMethod { get; set; }
    public DateTime CreatedAt { get; set; }
    public required IReadOnlyList<ProductDTO> Products { get; set; }

    // other attributes such as address, applied discounts, shipping details, transaction details and etc.
}

public class ProductDTO
{
    public int ProductId { get; set; }
    public required string Name { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}