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
    private readonly string? _filePath;

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

        _filePath = applicationSettings.TelegramFilesPath;
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

    public async Task<Stream?> GetMediaFileStreamAsync(string fileId, CancellationToken cancellationToken)
    {
        Stream? result = null;
        var fileResponse = await GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (fileResponse?.FilePath is not null)
        {
            Log.Information("FilePath:{filePath}, botToken:{botToken}, FullFilePath:{FullFilePath}", _filePath, _botToken, fileResponse.FilePath);
            result = string.IsNullOrEmpty(_filePath)
                ? await DownloadFileAsync(fileResponse.FilePath, cancellationToken).ConfigureAwait(false)
                : GetFileFromDisk(_filePath, Path.Combine(_botToken, fileResponse.FilePath));
        }
        else
        {
            Log.Error("Не получен path для файла {fileId}.", fileId);
        }

        return result;
    }

    /// <summary>
    /// Скачивает файл с серверов Telegram.
    /// </summary>
    /// <param name="mountPath">Путь к папке с файлами.</param>
    /// <param name="filePath">Путь к файлу, полученный из GetFileAsync.</param>
    /// <returns>Поток с данными файла.</returns>
    private static FileStream GetFileFromDisk(string mountPath, string filePath)
    {
        var fullPath = Path.Combine(mountPath, filePath);
        if (File.Exists(fullPath))
        {
            return File.OpenRead(fullPath);
        }
        else
        {
            throw new FileNotFoundException($"File not found at {fullPath}");
        }
    }

    /// <summary>
    /// Получает информацию о файле в Telegram.
    /// </summary>
    /// <param name="fileId">Идентификатор файла.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Информация о файле или null при ошибке.</returns>
    private async Task<FileResponse?> GetFileAsync(string fileId, CancellationToken cancellationToken)
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
    private async Task<Stream> DownloadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var url = $"/file/bot{_botToken}/{filePath}";
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }
}
