namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed class ProcessedMedia : IDisposable
{
    public required string MatrixMessageType { get; set; }

    public Stream? MediaFileStream { get; set; }

    public Stream? ThumbnailFileStream { get; set; }

    public required MediaInfo MediaInfo { get; set; }

    public void Dispose()
    {
        MediaFileStream?.Dispose();
        ThumbnailFileStream?.Dispose();
    }
}
