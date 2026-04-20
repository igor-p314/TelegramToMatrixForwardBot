namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed class MediaInfo
{
    public required string FileId { get; set; }

    public string? FileName { get; set; }

    public string? Caption { get; set; }

    public string? FormattedCaption { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? Duration { get; set; }

    public int? FileSize { get; set; }

    public string? MimeType { get; set; }

    public Qualitiy[]? Qualities { get; set; }

    public bool? IsVideo { get; set; }

    public MediaInfo? Thumbnail { get; set; }
}
