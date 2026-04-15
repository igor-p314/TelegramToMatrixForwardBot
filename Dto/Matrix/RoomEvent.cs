namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record RoomEvent
{
    public string? EventId { get; set; }

    public required string Type { get; set; }

    public required string Sender { get; set; }

    public required MessageContent Content { get; set; }

    public long? OriginServerTs { get; set; }
}
