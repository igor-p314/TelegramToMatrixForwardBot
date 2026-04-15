namespace TelegramToMatrixForward.Dto.Matrix;

internal sealed record LoginResult(
    string? AccessToken,
    string? UserId,
    string? HomeServer);
