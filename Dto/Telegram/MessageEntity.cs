namespace TelegramToMatrixForward.Dto.Telegram;

/// <summary>
/// Представляет entity форматирования в сообщении Telegram.
/// </summary>
internal sealed record MessageEntity
{
    /// <summary>
    /// Тип entity: bold, italic, underline, strikethrough, code, pre, text_link, custom_emoji и т.д.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Смещение начала entity в UTF-16 кодовых единицах.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Длина entity в UTF-16 кодовых единицах.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// URL для ссылок (только для text_link).
    /// </summary>
    public string? Url { get; set; }
}
