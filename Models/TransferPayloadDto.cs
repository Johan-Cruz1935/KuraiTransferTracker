using System.Text.Json.Serialization;

namespace KuraiTransferTracker.Models;

/// <summary>
/// Estructura raíz del JSON requerida por el sistema receptor:
/// { "transfers": [ ... ] }
/// </summary>
public sealed class TransferBatchPayloadDto
{
    [JsonPropertyName("transfers")]
    public List<TransferDto> Transfers { get; init; } = [];
}

public sealed class TransferDto
{
    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("business_date")]
    public string BusinessDate { get; init; } = string.Empty;

    [JsonPropertyName("origin_warehouse_id")]
    public string OriginWarehouseId { get; init; } = string.Empty;

    [JsonPropertyName("destination_warehouse_id")]
    public string DestinationWarehouseId { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public List<TransferItemDto> Items { get; init; } = [];
}

public sealed class TransferItemDto
{
    [JsonPropertyName("product_id")]
    public string ProductId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public string Quantity { get; init; } = string.Empty;
}
