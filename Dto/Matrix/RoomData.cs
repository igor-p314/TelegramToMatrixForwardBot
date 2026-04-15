namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record RoomData
{
    public TimelineData? Timeline { get; set; }
}
