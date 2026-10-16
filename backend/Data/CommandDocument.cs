using System.Text.Json;

namespace RollAndPay.Api.Data;

public sealed record CommandDocument
{
    public required string Id { get; init; }
    public required string OwnerKey { get; init; }
    public required string Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required Guid GameId { get; init; }
    public required string Operation { get; init; }
    public required string RequestHash { get; init; }
    public required int ResponseStatus { get; init; }
    public required string ResponseEtag { get; init; }
    public required string? ResponseLocation { get; init; }
    public required JsonElement ResponseBody { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
