using System.Text.Json.Serialization;

namespace TelegramToMatrixForward.Dto.Matrix;

internal sealed record MediaInfo(
    string? MimeType,
    long? Size,
    [property: JsonPropertyName("w")] int? Width,
    [property: JsonPropertyName("h")] int? Height,
    int? Duration);
