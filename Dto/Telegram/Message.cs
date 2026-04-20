using System.Text.Json.Serialization;

namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed class Message
{
    public int MessageId { get; set; }

    public User? From { get; set; }

    public long ChatId { get; set; }

    public string? Text { get; set; }

    public MediaInfo[]? Photo { get; set; }

    public MediaInfo? Document { get; set; }

    public MediaInfo? Video { get; set; }

    public MediaInfo? Audio { get; set; }

    public MediaInfo? Voice { get; set; }

    public MediaInfo? VideoNote { get; set; }

    public MediaInfo? Sticker { get; set; }

    public string? Caption { get; set; }

    public MessageEntity[]? CaptionEntities { get; set; }

    public MessageEntity[]? Entities { get; set; }

    public User? ForwardFrom { get; set; }

    public Chat? ForwardFromChat { get; set; }

    /// <summary>
    /// Медиа после обработки TelegramService.
    /// </summary>
    [property: JsonIgnore]
    public ProcessedMedia? ProcessedMedia { get; set; }

    public long GetMediaSize(int maxFileSize)
    {
        return GetPhoto(maxFileSize)?.FileSize
            ?? Document?.FileSize
            ?? Video?.FileSize
            ?? Audio?.FileSize
            ?? Voice?.FileSize
            ?? VideoNote?.FileSize
            ?? Sticker?.FileSize ?? 0L;
    }

    public MediaInfo? GetPhoto(int maxFileSize)
    {
        return Photo?.OrderByDescending(p => p.FileSize)
            .FirstOrDefault(p => p.FileSize < maxFileSize);
    }
}
