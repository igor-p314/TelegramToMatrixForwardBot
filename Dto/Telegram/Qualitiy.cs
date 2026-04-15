namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed class Qualitiy
{
    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Codec { get; set; }

    public required string FileId { get; set; }

    public int? FileSize { get; set; }
}
