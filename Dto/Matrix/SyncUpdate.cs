namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record SyncUpdate
{
    public string? NextBatch { get; set; }

    public RoomsData? Rooms { get; set; }
}
