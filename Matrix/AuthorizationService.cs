namespace TelegramToMatrixForward.Matrix;

using Serilog;
using System;
using System.Net.Http.Json;
using TelegramToMatrixForward.Dto;
using TelegramToMatrixForward.Dto.Matrix;

/// <summary>
/// Сервис авторизации бота в Matrix.
/// </summary>
/// <param name="httpService">HTTP-клиент для Matrix API.</param>
internal sealed class AuthorizationService(MatrixApiService httpService)
{
    /// <summary>
    /// HTTP-клиент для Matrix API.
    /// </summary>
    private readonly MatrixApiService _httpService = httpService;

    /// <summary>
    /// Логин бота. Значение из переменной среды MATRIX_BOT_USER_LOGIN.
    /// </summary>
    private readonly string _login = Environment.GetEnvironmentVariable("MATRIX_BOT_USER_LOGIN")
            ?? throw new InvalidOperationException("Не задана переменная среды MATRIX_BOT_USER_LOGIN");

    /// <summary>
    /// Пароль бота. Значение из переменной среды MATRIX_BOT_USER_PASSWORD.
    /// </summary>
    private readonly string _password = Environment.GetEnvironmentVariable("MATRIX_BOT_USER_PASSWORD")
            ?? throw new InvalidOperationException("Не задана переменная среды MATRIX_BOT_USER_PASSWORD");

    /// <summary>
    /// Идентификатор пользователя Matrix после авторизации.
    /// </summary>
    internal string UserId { get; private set; } = string.Empty;

    /// <summary>
    /// Выполняет авторизацию бота в Matrix.
    /// </summary>
    /// <param name="url">URL для авторизации.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Access token для последующих запросов.</returns>
    internal async ValueTask<string> AuthorizeAsync(string url, CancellationToken cancellationToken = default)
    {
        var request = new LoginRequest(User: _login, Password: _password);
        var content = JsonContent.Create(request, MatrixJsonContext.Default.LoginRequest);

        HttpResponseMessage response = await _httpService.PostNoRetryAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var loginResult = System.Text.Json.JsonSerializer.Deserialize(responseString, MatrixJsonContext.Default.LoginResult);

        var result = loginResult?.AccessToken ?? throw new InvalidOperationException("Authorization Error.");
        UserId = loginResult?.UserId ?? throw new InvalidOperationException("Authorization Error.");

        Log.Information("Бот {UserId} успешно авторизован в Matrix.", UserId);

        return result;
    }
}
