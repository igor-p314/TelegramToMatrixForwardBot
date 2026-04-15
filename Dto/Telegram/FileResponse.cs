namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed record FileResponse(
    string FileId,
    string? FilePath,
    long? FileSize);
