using Serilog;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using TelegramToMatrixForward.Dto;
using TelegramToMatrixForward.Dto.Matrix;
using TelegramToMatrixForward.Dto.Telegram;
using TelegramToMatrixForward.Storage;
using TelegramToMatrixForward.Telegram;
using TelegramMessage = TelegramToMatrixForward.Dto.Telegram.Message;

namespace TelegramToMatrixForward.Matrix;

/// <summary>
/// Сервис работы с Matrix: sync loop, отправка сообщений, upload файлов.
/// </summary>
internal sealed class MatrixService
{
    private const int MaxAllowedUsersInRoom = 2;

    private readonly int _maxMessageAge = 14_400_000; // 4 hours

    private readonly MatrixApiService _apiService;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly LinkService _linkService;

    private readonly ChannelReader<TelegramMessage> _toMatrixChannel;
    private readonly ChannelWriter<ToTelegramMessage> _toTelegramChannel;

    private readonly string _batchTokenPath;
    private readonly long _messageRetention;
    private readonly ApplicationSettings _applicationSettings;

    /// <summary>
    /// Создаёт экземпляр сервиса Matrix.
    /// </summary>
    /// <param name="linkService">Сервис управления связями.</param>
    /// <param name="applicationSettings">Настройки приложения.</param>
    public MatrixService(
        LinkService linkService,
        ApplicationSettings applicationSettings)
    {
        _linkService = linkService;
        _toTelegramChannel = Program.ToTelegramChannel.Writer;
        _toMatrixChannel = Program.ToMatrixChannel.Reader;
        _apiService = new MatrixApiService(applicationSettings);

        var tempString = Environment.GetEnvironmentVariable("MATRIX_BOT_MAX_MESSAGE_AGE_MS");
        if (!string.IsNullOrEmpty(tempString))
        {
            _maxMessageAge = int.Parse(tempString);
        }

        _batchTokenPath = applicationSettings.MatrixBatchTokenPath;
        _messageRetention = applicationSettings.MessageRetentionPolicyInMilliseconds;
        _applicationSettings = applicationSettings;
    }

    /// <summary>
    /// Запускает цикл синхронизации с Matrix сервером.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        var authorizationService = await _apiService.AuthorizeAsync("/_matrix/client/v3/login", cancellationToken).ConfigureAwait(false);

        _ = ListenTelegramToMatrixChannelAsync(cancellationToken); // fire and forget

        await ConnectToServerAsync(authorizationService, cancellationToken).ConfigureAwait(false);
    }

    private static string GetMatrixServerName(string matrixUserId)
    {
        return matrixUserId.Split(':').LastOrDefault() ?? throw new InvalidOperationException($"{matrixUserId} - неверный идентификатор пользователя.");
    }

    /// <summary>
    /// Отправляет текстовое сообщение в Matrix комнату.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты Matrix.</param>
    /// <param name="message">Сообщени для отправки.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task SendMessageToRoomAsync(string roomKey, Message message, CancellationToken cancellationToken)
    {
        await SendToRoomAsync(roomKey, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Загружает файл в Matrix и получает MXC URI.
    /// </summary>
    /// <param name="fileStream">Поток с данными файла.</param>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="mimeType">MIME-тип файла.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>MXC URI загруженного файла или null при ошибке.</returns>
    private async Task<string?> UploadFileAsync(Stream fileStream, string fileName, string mimeType, CancellationToken cancellationToken)
    {
        var uploadUrl = $"/_matrix/media/v3/upload?filename={Uri.EscapeDataString(fileName)}";
        string? result = null;
        var content = new StreamContent(fileStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);

        var response = await _apiService.PostAsync(uploadUrl, content, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            Log.Information("Загрузка файла в matrix: {statusCode}", response.StatusCode);

            var uploadResult = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Deserialize(uploadResult, MatrixJsonContext.Default.DictionaryStringString);
            result = json?.GetValueOrDefault("content_uri");
        }
        else
        {
            Log.Error(
                "Ошибка загрузки файла в Matrix: {statusCode} - {response}",
                response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Отправляет сообщение в комнату Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="json">Сообщение в формате json.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private Task<HttpResponseMessage> SendToRoomAsync(string roomKey, JsonContent json, CancellationToken cancellationToken)
    {
        var txnId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
        var sendUrl = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomKey)}/send/m.room.message/{txnId}";

        return _apiService.PutAsync(sendUrl, json, cancellationToken);
    }

    private async Task ListenTelegramToMatrixChannelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _toMatrixChannel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            Log.Error(ex, "Отмена чтения канала свящи Matrix->Telegram.");
        }
        finally
        {
            Log.Information("Слушание канала Matrix->Telegram завершено.");
        }
    }

    private async Task ProcessMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message.From, "message.From");

        var telegramUserId = message.From.Id;
        var matrixRoomKey = _linkService.GetMatrixRoomKey(telegramUserId);
        if (!string.IsNullOrEmpty(matrixRoomKey))
        {
            await SendMessageToMatrixAsync(message, matrixRoomKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _toTelegramChannel.WriteAsync(
                new ToTelegramMessage
                {
                    ChatId = message.ChatId,
                    Text = "❌ Вы не связаны с Matrix. Отправьте /start для настройки связи.",
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendMessageToMatrixAsync(TelegramMessage message, string matrixRoomKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message.From, "message.From");

        var username = TelegramEntitiesParser.EscapeHtmlAttribute(
                            message.ForwardFrom?.Username
                            ?? message.ForwardFromChat?.Username
                            ?? message.From.Username
                            ?? "Unknown");
        var firstName = message.ForwardFrom?.FirstName ?? message.ForwardFromChat?.Title ?? message.From.FirstName;
        var lastName = message.ForwardFromChat is null ? message.ForwardFrom?.LastName ?? message.From?.LastName : null;
        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Unknown";
        }
        else
        {
            displayName = TelegramEntitiesParser.EscapeHtmlAttribute(displayName);
        }

        var prefix = $"[Telegram: {displayName} @{username}]\n\n";
        var formattedPrefix = $"[Telegram: <a href=\"https://t.me/{username}\">{displayName}</a>]\n\n<br />";

        if (!string.IsNullOrEmpty(message.Text))
        {
            var text = $"{prefix}{message.Text}";
            var formattedText = $"{formattedPrefix}{TelegramEntitiesParser.ParseToHtml(message.Text, message.Entities)}";
            await SendMessageToRoomAsync(
                matrixRoomKey,
                new FormattedMessage(formattedText, text),
                cancellationToken).ConfigureAwait(false);
        }

        if (message.ProcessedMedia is not null)
        {
            message.ProcessedMedia.MediaInfo.Caption = $"{prefix}{message.Caption}";
            message.ProcessedMedia.MediaInfo.FormattedCaption =
                $"{formattedPrefix}{TelegramEntitiesParser.ParseToHtml(message.Caption, message.CaptionEntities)}";

            await SendMediaToMatrixAsync(message.ProcessedMedia, matrixRoomKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendMediaToMatrixAsync(
        ProcessedMedia media,
        string roomKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media.MediaInfo.FileId, "mediaInfo.FileId");
        ArgumentNullException.ThrowIfNull(media.MediaInfo.FileName, "mediaInfo.FileName");
        ArgumentNullException.ThrowIfNull(media.MediaInfo.MimeType, "mediaInfo.MimeType");
        ArgumentNullException.ThrowIfNull(media.MatrixMessageType, "mediaInfo.MatrixMessageType");
        ArgumentNullException.ThrowIfNull(media.MediaInfo.Caption, "mediaInfo.Caption");
        ArgumentNullException.ThrowIfNull(media.MediaInfo.FormattedCaption, "mediaInfo.FormattedCaption");

        try
        {
            if (media.MediaFileStream is not null)
            {
                var mxcUri = await UploadFileAsync(media.MediaFileStream, media.MediaInfo.FileName, media.MediaInfo.MimeType, cancellationToken)
                    .ConfigureAwait(false);
                if (mxcUri is not null)
                {
                    var messageContent = new Dictionary<string, object>
                    {
                        { "msgtype", media.MatrixMessageType },
                        { "filename", media.MediaInfo.FileName },
                        { "body", media.MediaInfo.Caption },
                        { "formatted_body", media.MediaInfo.FormattedCaption },
                        { "format", "org.matrix.custom.html" },
                        { "url", mxcUri },
                        {
                            "info", new Dictionary<string, object>
                            {
                                { "mimetype", media.MediaInfo.MimeType },
                                { "size", media.MediaInfo.FileSize.GetValueOrDefault(0) },
                            }
                        },
                    };

                    if (media.MediaInfo.Thumbnail is not null
                        && media.MediaInfo.Thumbnail.Width is not null
                        && media.MediaInfo.Thumbnail.Height is not null)
                    {
                        if (media.ThumbnailFileStream is not null)
                        {
                            var thumbnailMxcUri = await UploadFileAsync(
                                    media.ThumbnailFileStream,
                                    media.MediaInfo.Thumbnail.FileName ?? media.MediaInfo.FileName,
                                    media.MediaInfo.Thumbnail.MimeType ?? "image/jpeg",
                                    cancellationToken).ConfigureAwait(false);
                            if (thumbnailMxcUri is not null)
                            {
                                ((Dictionary<string, object>)messageContent["info"]).Add(
                                    "thumbnail_info",
                                    new Dictionary<string, object>
                                    {
                                        { "mimetype", media.MediaInfo.Thumbnail.MimeType ?? "image/jpeg" },
                                        { "w", media.MediaInfo.Thumbnail.Width },
                                        { "h", media.MediaInfo.Thumbnail.Height },
                                        { "size", media.MediaInfo.Thumbnail.FileSize.GetValueOrDefault(0) },
                                    });
                                ((Dictionary<string, object>)messageContent["info"]).Add("thumbnail_url", thumbnailMxcUri);
                            }
                        }
                    }

                    if (media.MediaInfo.Width.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add("w", media.MediaInfo.Width.Value);
                    }

                    if (media.MediaInfo.Height.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add("h", media.MediaInfo.Height.Value);
                    }

                    if (media.MediaInfo.Duration.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add(
                            "duration",
                            media.MediaInfo.Duration.Value * ApplicationSettings.MillisecondsInOneSecond);
                    }

                    var content = JsonContent.Create(messageContent, MatrixJsonContext.Default.DictionaryStringObject);
                    var response = await SendToRoomAsync(roomKey, content, cancellationToken).ConfigureAwait(false);

                    Log.Information("Медиа отправлено в комнату {roomKey}: {statusCode}", roomKey, response.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка отправки медиа в Matrix.");
        }
    }

    /// <summary>
    /// Подключается к Matrix серверу и запускает цикл синхронизации.
    /// </summary>
    /// <param name="authorizationService">Сервис авторизации.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task ConnectToServerAsync(AuthorizationService authorizationService, CancellationToken cancellationToken)
    {
        string? batchFromFile = null;
        if (!string.IsNullOrEmpty(_batchTokenPath) && File.Exists(_batchTokenPath))
        {
            batchFromFile = await File.ReadAllTextAsync(_batchTokenPath, cancellationToken).ConfigureAwait(false);
        }

        string url;
        string nextBatch;
        if (string.IsNullOrEmpty(batchFromFile))
        {
            url = "/_matrix/client/v3/sync";
        }
        else
        {
            nextBatch = batchFromFile;
            url = $"/_matrix/client/v3/sync?since={Uri.EscapeDataString(nextBatch)}&timeout={_applicationSettings.PollTimeoutMilliseconds}";
        }

        var response = await _apiService.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        nextBatch = await ProcessSyncDataResponseAsync(response, authorizationService.UserId, cancellationToken).ConfigureAwait(false);

        Program.MatrixServiceReady.SetResult(true);
        await Program.TelegramServiceReady.Task.ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested && !string.IsNullOrEmpty(nextBatch))
        {
            try
            {
                if (!string.IsNullOrEmpty(_batchTokenPath))
                {
                    await File.WriteAllTextAsync(_batchTokenPath, nextBatch, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Log.Information("Не задан путь к Matrix Batch Token");
                }

                url = $"/_matrix/client/v3/sync?since={Uri.EscapeDataString(nextBatch)}&timeout={_applicationSettings.PollTimeoutMilliseconds}";
                response = await _apiService.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                nextBatch = await ProcessSyncDataResponseAsync(response, authorizationService.UserId, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // timeout is fine
            }
        }

        Log.Information("Disconnected from Matrix.");
    }

    /// <summary>
    /// Обрабатывает ответ от сервера синхронизации.
    /// </summary>
    /// <param name="response">JSON-ответ сервера.</param>
    /// <param name="currentUserId">Идентификатор текущего пользователя (бота).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Токен следующей партии синхронизации.</returns>
    private async Task<string> ProcessSyncDataResponseAsync(string response, string currentUserId, CancellationToken cancellationToken)
    {
        var syncData = JsonSerializer.Deserialize(response, MatrixJsonContext.Default.SyncUpdate)
            ?? throw new InvalidOperationException("Failed to deserialize sync data.");

        if (syncData.Rooms is not null)
        {
            if (syncData.Rooms.Invite.Count > 0)
            {
                await ProcessInvitesAsync(syncData.Rooms.Invite, cancellationToken).ConfigureAwait(false);
            }

            if (syncData.Rooms.Join?.Count > 0)
            {
                var messages = syncData.Rooms.Join
                    .Where(r => r.Value.Timeline?.Events.Count > 0)
                    .SelectMany(r => r.Value.Timeline!.Events
                        .Where(e => e.Type == "m.room.message"
                                && !string.IsNullOrEmpty(e.Content.Body)
                                && !currentUserId.Equals(e.Sender, StringComparison.OrdinalIgnoreCase)
                                && _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - e.OriginServerTs < _maxMessageAge)
                    .Select(e => (roomKey: r.Key, text: e.Content.Body!, sender: e.Sender, content: e.Content)));

                foreach (var (roomKey, text, sender, content) in messages)
                {
                    if (_apiService.HomeServerUrl.Equals(GetMatrixServerName(sender), StringComparison.OrdinalIgnoreCase))
                    {
                        await ProcessMessageAsync(roomKey, text, sender, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await LeaveRoomAsync(roomKey, cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var room in syncData.Rooms.Join)
                {
                    var lastEvent = room.Value.Timeline?.Events
                        .OrderByDescending(e => e.OriginServerTs)
                        .FirstOrDefault();
                    if (lastEvent?.EventId is not null)
                    {
                        await SetReadMarkerAsync(room.Key, lastEvent.EventId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        return syncData.NextBatch ?? throw new InvalidOperationException("Sync data does not contain next batch token.");
    }

    /// <summary>
    /// Обрабатывает сообщение из Matrix комнаты.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты Matrix.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="sender">Идентификатор отправителя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task ProcessMessageAsync(string roomKey, string text, string sender, CancellationToken cancellationToken)
    {
        var command = text.Trim();
        switch (command)
        {
            case string cmd when "!start ".StartsWith(cmd, StringComparison.OrdinalIgnoreCase)
                                    || "/start ".Equals(cmd, StringComparison.OrdinalIgnoreCase):
                await SendToRoomAsync(roomKey, new Message("Введите 3-значный код из Telegram."), cancellationToken).ConfigureAwait(false);
                break;
            default:
                var telegramUserId = _linkService.GetTelegramUserId(sender);
                if (telegramUserId.HasValue
                    && await _linkService.TryLinkByCodeAsync(command, roomKey, cancellationToken).ConfigureAwait(false))
                {
                    await SendToRoomAsync(
                        roomKey,
                        new Message("✅ Связь установлена! Теперь ваши сообщения из Telegram будут пересылаться сюда."),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendToRoomAsync(roomKey, new Message("❌ Код не найден. Попробуйте ещё раз."), cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>
    /// Обрабатывает входящие приглашения в комнаты.
    /// </summary>
    /// <param name="invites">Словарь приглашений (roomKey → данные).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task ProcessInvitesAsync(Dictionary<string, InviteData> invites, CancellationToken cancellationToken)
    {
        foreach (var invite in invites)
        {
            var membersCount = invite.Value.InviteState.Events.Count(e => e.Type == "m.room.member");
            var roomName = invite.Value.InviteState.Events.FirstOrDefault(e => e.Type == "m.room.name")?.Content?.Name ?? "Unknown";
            var isEncrypted = invite.Value.InviteState.Events.Any(e => e.Type == "m.room.encryption");
            var sender = invite.Value.InviteState.Events.FirstOrDefault(e => e.Content?.Membership == "invite")?.Sender ?? string.Empty;

            if (membersCount == MaxAllowedUsersInRoom
                && !isEncrypted
                && _apiService.HomeServerUrl.Equals(GetMatrixServerName(sender), StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => JoinDirectRoomAsync(invite.Key, cancellationToken), cancellationToken);
            }
            else
            {
                _ = Task.Run(() => LeaveRoomAsync(invite.Key, cancellationToken), cancellationToken);
                Log.Information(
                    "Отклонено приглашение в комнату '{roomName}'. Количество участников: {membersCount}, IsEncrypted = {isEncrypted}.",
                    roomName,
                    membersCount,
                    isEncrypted);
            }
        }
    }

    /// <summary>
    /// Входит в комнату Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task JoinDirectRoomAsync(string roomKey, CancellationToken cancellationToken)
    {
        var joinUrl = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomKey)}/join";
        var response = await _apiService.PostAsync(joinUrl, null, cancellationToken).ConfigureAwait(false);
        Log.Information("Вход в комнату {roomKey}: {statusCode}", roomKey, response.StatusCode);

        await SendRetentionToRoomAsync(roomKey, _messageRetention, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Покидает комнату Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task LeaveRoomAsync(string roomKey, CancellationToken cancellationToken)
    {
        var leaveUrl = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomKey)}/leave";
        var response = await _apiService.PostAsync(leaveUrl, null, cancellationToken).ConfigureAwait(false);
        Log.Information("Уход из комнаты {roomKey}: {statusCode}", roomKey, response.StatusCode);
    }

    /// <summary>
    /// Отправляет сообщение в комнату Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="message">Сообщение для отправки.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task SendToRoomAsync(string roomKey, Message message, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(message.ToSerializableMessage(), MatrixJsonContext.Default.DictionaryStringString);
        var response = await SendToRoomAsync(roomKey, content, cancellationToken).ConfigureAwait(false);

        Log.Information("Сообщение отправлено в комнату {roomKey}: {statusCode}", roomKey, response.StatusCode);
    }

    /// <summary>
    /// Отправляет сообщение в комнату Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="retentionInMilliseconds">Время хранения сообщений в миллисекундах.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task SendRetentionToRoomAsync(string roomKey, long retentionInMilliseconds, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(
                new Dictionary<string, object>
                {
                    { "max_lifetime", retentionInMilliseconds },
                },
                MatrixJsonContext.Default.DictionaryStringObject);
        var sendUrl = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomKey)}/state/m.room.retention";
        var response = await _apiService.PutAsync(sendUrl, content, cancellationToken).ConfigureAwait(false);

        Log.Information("Установка времени жизни сообщения в комнате {roomKey}: {statusCode}", roomKey, response.StatusCode);
    }

    /// <summary>
    /// Устанавливает маркер прочтения в комнате Matrix.
    /// </summary>
    /// <param name="roomKey">Идентификатор комнаты.</param>
    /// <param name="eventId">Идентификатор события.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private async Task SetReadMarkerAsync(string roomKey, string eventId, CancellationToken cancellationToken)
    {
        var url = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomKey)}/read_markers";

        var content = JsonContent.Create(
            new Dictionary<string, string>
            {
                { "m.fully_read", eventId },
                { "m.read", eventId },
            },
            MatrixJsonContext.Default.DictionaryStringString);

        await _apiService.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
    }
}
