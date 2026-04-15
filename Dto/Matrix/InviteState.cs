namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record InviteState
{
    public IReadOnlyCollection<RoomEvent> Events { get; set; } = [];
}
