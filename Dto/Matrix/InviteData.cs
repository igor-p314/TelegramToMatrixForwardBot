namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record InviteData
{
    public required InviteState InviteState { get; set; }
}
