using Serilog;
using System.Text.Json;
using TelegramToMatrixForward.Dto;
using TelegramToMatrixForward.Dto.Telegram;

namespace TelegramToMatrixForward.Telegram;

/// <summary>
/// Сервис работы с Telegram Bot API через HttpClient.
/// </summary>
internal sealed class TelegramApiService
{
    private const int AdditionalSecondsToTimeout = 5;

    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly int _pollTimeoutSeconds;

    /// <summary>
    /// Создаёт экземпляр сервиса Telegram API.
    /// </summary>
    /// <param name="applicationSettings">Настройки приложения.</param>
    public TelegramApiService(ApplicationSettings applicationSettings)
    {
        _botToken = applicationSettings.TelegramBotToken;
        _pollTimeoutSeconds = applicationSettings.PollTimeoutMilliseconds / ApplicationSettings.MillisecondsInOneSecond;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(applicationSettings.TelegramApiUrl),
            Timeout = TimeSpan.FromSeconds(_pollTimeoutSeconds + AdditionalSecondsToTimeout),
        };
    }

    /// <summary>
    /// Получает обновления через long polling.
    /// </summary>
    /// <param name="offset">Смещение для получения следующих обновлений.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Массив обновлений.</returns>
    public async Task<Update[]> GetUpdatesAsync(int offset, CancellationToken cancellationToken)
    {
        var url = $"/bot{_botToken}/getUpdates?offset={offset}&timeout={_pollTimeoutSeconds}&allowed_updates=[\"message\"]";

        await HealthService.HeartBeatTelegramAsync(cancellationToken).ConfigureAwait(false);

        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdate);

        if (result is not { Ok: true })
        {
            var error = result?.Description ?? "Unknown error";
            Log.Error("Telegram API error: {error}", error);
            throw new InvalidOperationException($"Telegram API error: {error}");
        }

        return result.Result ?? [];
    }

    /// <summary>
    /// Отправляет текстовое сообщение в чат Telegram.
    /// </summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var url = $"/bot{_botToken}/sendMessage";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("chat_id", chatId.ToString()),
            new KeyValuePair<string, string>("text", text),
            new KeyValuePair<string, string>("parse_mode", "MarkdownV2"),
        });

        var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Ошибка отправки сообщения: {response}", responseContent);
        }
    }

    /// <summary>
    /// Получает информацию о файле в Telegram.
    /// </summary>
    /// <param name="fileId">Идентификатор файла.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Информация о файле или null при ошибке.</returns>
    public async Task<FileResponse?> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var url = $"/bot{_botToken}/getFile?file_id={Uri.EscapeDataString(fileId)}";
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseFileResponse);

        return result?.Ok == true ? result.Result : null;
    }

    /// <summary>
    /// Скачивает файл с серверов Telegram.
    /// </summary>
    /// <param name="filePath">Путь к файлу, полученный из GetFileAsync.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Поток с данными файла.</returns>
    public async Task<Stream> DownloadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var url = $"/file/bot{_botToken}/{filePath}";
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }
}
