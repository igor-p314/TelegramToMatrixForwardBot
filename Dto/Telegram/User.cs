namespace TelegramToMatrixForward.Dto.Telegram;

internal sealed record User(
    long Id,
    string? FirstName,
    string? LastName,
    string? Username);
