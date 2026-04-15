namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record RoomsData
{
    public Dictionary<string, RoomData> Join { get; set; } = [];

    public Dictionary<string, InviteData> Invite { get; set; } = [];
}
