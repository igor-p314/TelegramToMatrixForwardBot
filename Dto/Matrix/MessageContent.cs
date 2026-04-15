namespace TelegramToMatrixForward.Dto.Matrix;

public sealed record MessageContent
{
    public string? MsgType { get; set; }

    public string? Body { get; set; }

    public string? Name { get; set; }

    public string? Membership { get; set; }
}
