using System.Text;
using TelegramToMatrixForward.Dto.Telegram;

namespace TelegramToMatrixForward.Telegram;

/// <summary>
/// Парсер для преобразования entities форматирования Telegram в HTML-формат Matrix.
/// </summary>
internal static class TelegramEntitiesParser
{
    private readonly record struct TagEntry(int Position, string Tag);

    /// <summary>
    /// Преобразует текст с entities Telegram в HTML-формат Matrix.
    /// </summary>
    /// <param name="text">Исходный текст сообщения.</param>
    /// <param name="entities">Массив entities форматирования.</param>
    /// <returns>HTML-представление текста для Matrix.</returns>
    public static string ParseToHtml(string? text, MessageEntity[]? entities)
    {
        var result = string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            if (entities is null || entities.Length == 0)
            {
                result = EscapeHtml(text);
            }
            else
            {
                var tags = new TagEntry[entities.Length * 2];
                var index = 0;
                foreach (var entity in entities)
                {
                    tags[index++] = new TagEntry(entity.Offset, GetOpenTag(entity.Type, entity.Url));
                    tags[index++] = new TagEntry(entity.Offset + entity.Length, GetCloseTag(entity.Type));
                }

                Array.Sort(tags, (a, b) => a.Position.CompareTo(b.Position));

                var resultBuilder = new StringBuilder(text.Length * 2);
                var currentPosition = 0;

                foreach (var tag in tags)
                {
                    var safePosition = Math.Clamp(tag.Position, 0, text.Length);
                    if (safePosition < currentPosition)
                    {
                        continue;
                    }

                    if (safePosition > currentPosition)
                    {
                        resultBuilder.Append(EscapeHtml(text.Substring(currentPosition, safePosition - currentPosition)));
                    }

                    resultBuilder.Append(tag.Tag);

                    currentPosition = safePosition;
                }

                if (currentPosition < text.Length)
                {
                    resultBuilder.Append(EscapeHtml(text.Substring(currentPosition)));
                }

                result = resultBuilder
                    .Replace("&#xA;", "<br />")
                    .Replace("&#10;", "<br />")
                    .ToString();
            }
        }

        return result;
    }

    internal static string EscapeHtml(string text)
    {
        var result = string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            result = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);
        }

        return result;
    }

    internal static string EscapeHtmlAttribute(string text)
    {
        var result = string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            result = text
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        return result;
    }

    private static string GetOpenTag(string type, string? url)
    {
        return type switch
        {
            "bold" => "<b>",
            "italic" => "<i>",
            "underline" => "<u>",
            "strikethrough" => "<del>",
            "code" => "<code>",
            "pre" => "<pre>",
            "blockquote" or "expandable_blockquote" => "<p><blockquote>",
            "text_link" when !string.IsNullOrEmpty(url)
                => $"<a href=\"{EscapeHtmlAttribute(url)}\">",
            "text_mention" when !string.IsNullOrEmpty(url)
                => $"<a href=\"{EscapeHtmlAttribute(url)}\">",
            "spoiler" => "<span data-mx-spoiler>",
            _ => string.Empty,
        };
    }

    private static string GetCloseTag(string type)
    {
        return type switch
        {
            "bold" => "</b>",
            "italic" => "</i>",
            "underline" => "</u>",
            "strikethrough" => "</del>",
            "code" => "</code>",
            "pre" => "</pre>",
            "blockquote" or "expandable_blockquote" => "</blockquote></p>",
            "text_link" => "</a>",
            "text_mention" => "</a>",
            "spoiler" => "</span>",
            _ => string.Empty,
        };
    }
}
