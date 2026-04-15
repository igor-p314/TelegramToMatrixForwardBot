namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record TimelineData
{
    public IReadOnlyCollection<RoomEvent> Events { get; set; } = [];
}
