using Serilog;
using System.Net.Http.Json;
using TelegramToMatrixForward.Dto;
using TelegramToMatrixForward.Dto.Telegram;
using TelegramToMatrixForward.Storage;

namespace TelegramToMatrixForward.Telegram;

/// <summary>
/// Сервис обработки сообщений Telegram и пересылки в Matrix.
/// </summary>
internal sealed class TelegramService
{
    private const int MaxVideoHeight = 720;

    private readonly string _offsetIdPath;

    private readonly TelegramApiService _apiService;
    private readonly Matrix.MatrixService _matrixService;
    private readonly LinkService _linkService;
    private readonly int _maxFileSizeInBytes;
    private readonly int _maxFileSizeMegaBytes;

    /// <summary>
    /// Создаёт экземпляр сервиса Telegram.
    /// </summary>
    /// <param name="apiService">Сервис Telegram Bot API.</param>
    /// <param name="matrixService">Сервис Matrix.</param>
    /// <param name="linkService">Сервис управления связями.</param>
    /// <param name="maxFileSizeMb">Максимальный размер файла для пересылки в МБ.</param>
    /// <param name="offsetIdPath">Путь к файлу с идентифкатором синхронизации чатов.</param>
    public TelegramService(
        TelegramApiService apiService,
        Matrix.MatrixService matrixService,
        LinkService linkService,
        int maxFileSizeMb,
        string offsetIdPath)
    {
        _apiService = apiService;
        _matrixService = matrixService;
        _linkService = linkService;
        _maxFileSizeMegaBytes = maxFileSizeMb;
        _offsetIdPath = offsetIdPath;
#pragma warning disable MEN010 // Байты килобайты
        _maxFileSizeInBytes = maxFileSizeMb * 1024 * 1024;
#pragma warning restore MEN010
    }

    /// <summary>
    /// Обрабатывает одно сообщение из Telegram.
    /// </summary>
    /// <param name="message">Сообщение из Telegram.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async ValueTask ProcessMessageAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message.From, "message.From");

        var telegramUserId = message.From.Id;
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

        switch (message.Text)
        {
            case string text when "/start".Equals(text?.Trim()):
                await HandleStartCommandAsync(telegramUserId, cancellationToken).ConfigureAwait(false);
                break;
            case string text when "/stop".Equals(text?.Trim()):
                await HandleStopCommandAsync(telegramUserId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                var matrixRoomKey = _linkService.GetMatrixRoomKey(telegramUserId);
                if (!string.IsNullOrEmpty(matrixRoomKey))
                {
                    var prefix = $"[Telegram: {displayName} @{username}]\n\n";
                    var formattedPrfix = $"[Telegram: <a href=\"https://t.me/{username}\">{displayName}</a>]\n\n<br />";
                    await ForwardToMatrixAsync(message, matrixRoomKey, prefix, formattedPrfix, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _apiService.SendMessageAsync(
                        message.ChatId,
                        "❌ Вы не связаны с Matrix. Отправьте /start для настройки связи.",
                        cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>
    /// Запускает цикл long polling для получения сообщений из Telegram.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var offset = 0;

        if (!string.IsNullOrEmpty(_offsetIdPath) && File.Exists(_offsetIdPath))
        {
            offset = int.Parse(await File.ReadAllTextAsync(_offsetIdPath, cancellationToken).ConfigureAwait(false));
        }

        Log.Information("Telegram bot запущен в режиме long polling.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _apiService.GetUpdatesAsync(offset, cancellationToken).ConfigureAwait(false);

                foreach (var update in updates)
                {
                    if (update.Message is not null && update.Message.From is not null)
                    {
                        offset = Math.Max(offset, update.UpdateId + 1);

                        await ProcessMessageAsync(update.Message, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(_offsetIdPath))
                        {
                            await File.WriteAllTextAsync(_offsetIdPath, offset.ToString(), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // timeout is fine
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка в цикле Telegram polling.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static MediaInfo SetPhotoDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "image/jpeg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "photo.jpg";
        }

        mediaInfo.MatrixMessageType = "m.image";

        return mediaInfo;
    }

    private static MediaInfo SetDocumentDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "application/octet-stream";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "document";
        }

        mediaInfo.MatrixMessageType = "m.file";

        return mediaInfo;
    }

    private static MediaInfo SetVideoDefaults(MediaInfo mediaInfo, long maxFileSizeInBytes)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "video/mp4";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "video.mp4";
        }

        mediaInfo.MatrixMessageType = "m.video";

        if (mediaInfo.Qualities?.Length > 0)
        {
            var better = mediaInfo.Qualities
                            .FirstOrDefault(q => q.FileSize < maxFileSizeInBytes && q.Height <= MaxVideoHeight)
                           ?? mediaInfo.Qualities.FirstOrDefault(q => q.FileSize < maxFileSizeInBytes);

            if (better is not null)
            {
                mediaInfo.FileSize = better.FileSize;
                mediaInfo.FileId = better.FileId;
                mediaInfo.Width = better.Width;
                mediaInfo.Height = better.Height;
            }
        }

        return mediaInfo;
    }

    private static MediaInfo SetAudioDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "audio/mpeg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "audio.mp3";
        }

        mediaInfo.MatrixMessageType = "m.audio";

        return mediaInfo;
    }

    private static MediaInfo SetVoiceDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "audio/ogg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "voice.ogg";
        }

        mediaInfo.MatrixMessageType = "m.audio";

        return mediaInfo;
    }

    private static MediaInfo SetVideoNoteDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "video/mp4";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "videonote.mp4";
        }

        mediaInfo.MatrixMessageType = "m.video";

        return mediaInfo;
    }

    private static MediaInfo SetStickerDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = mediaInfo.IsVideo.GetValueOrDefault() ? "video/webm" : "image/webp";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = mediaInfo.IsVideo.GetValueOrDefault() ? "sticker.webm" : "sticker.webp";
        }

        mediaInfo.MatrixMessageType = mediaInfo.IsVideo.GetValueOrDefault() ? "m.video" : "m.image";

        return mediaInfo;
    }

    private async ValueTask HandleStartCommandAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var matrixRoomKey = _linkService.GetMatrixRoomKey(telegramUserId);
        if (string.IsNullOrEmpty(matrixRoomKey))
        {
            var code = _linkService.GeneratePendingCode(telegramUserId);
            await _apiService.SendMessageAsync(
                telegramUserId,
                $"🔗 Ваш код: *{code}*\n\nОтправьте `\\!start` боту в Matrix и введите этот код\\.",
                cancellationToken).ConfigureAwait(false);

            Log.Information("Код отправлен пользователю.");
        }
        else
        {
            await _apiService.SendMessageAsync(telegramUserId, "✅ Вы уже связаны с Matrix. Для отключения связи используйте /stop.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask HandleStopCommandAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        await _linkService.UnlinkAsync(telegramUserId, cancellationToken).ConfigureAwait(false);
        await _apiService.SendMessageAsync(telegramUserId, "✅ Связь с Matrix удалена.", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ForwardToMatrixAsync(
        Message message,
        string roomKey,
        string prefix,
        string formattedPrefix,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(message.Text))
        {
            var formattedText = $"{formattedPrefix}{TelegramEntitiesParser.ParseToHtml(message.Text, message.Entities)}";
            await _matrixService.SendMessageToRoomAsync(
                roomKey,
                new Matrix.FormattedMessage(formattedText, message.Text),
                cancellationToken).ConfigureAwait(false);
        }

        var mediaSize = message.GetMediaSize(_maxFileSizeInBytes);
        if (mediaSize > 0)
        {
            if (mediaSize < _maxFileSizeInBytes)
            {
                await SendMediaToMatrixAsync(message, roomKey, prefix, formattedPrefix, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _apiService.SendMessageAsync(
                    message.ChatId,
                    $"❌ Размер пересылаемого медиа превышает установленный лимит в {_maxFileSizeMegaBytes} мбайт.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask SendMediaToMatrixAsync(
        Message message,
        string roomKey,
        string plainPrefix,
        string formattedPrefix,
        CancellationToken cancellationToken)
    {
        MediaInfo mediaToSend = message switch
        {
            { Photo.Length: > 0 } => SetPhotoDefaults(message.GetPhoto(_maxFileSizeInBytes)!),
            { Document: { } doc } => SetDocumentDefaults(doc),
            { Video: { } video } => SetVideoDefaults(video, _maxFileSizeInBytes),
            { Audio: { } audio } => SetAudioDefaults(audio),
            { Voice: { } voice } => SetVoiceDefaults(voice),
            { VideoNote: { } vn } => SetVideoNoteDefaults(vn),
            { Sticker: { } sticker } => SetStickerDefaults(sticker),
            _ => throw new InvalidOperationException("Неизвестный тип медиа."),
        };

        mediaToSend.Caption = $"{plainPrefix}{message.Caption}";
        mediaToSend.FormattedCaption = $"{formattedPrefix}{TelegramEntitiesParser.ParseToHtml(message.Caption, message.CaptionEntities)}";

        await SendMediaToMatrixAsync(mediaToSend, roomKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream?> GetMediaFileStreamAsync(string fileId, CancellationToken cancellationToken)
    {
        Stream? result = null;
        var fileResponse = await _apiService.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (fileResponse?.FilePath is not null)
        {
            result = await _apiService.DownloadFileAsync(fileResponse.FilePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Log.Error("Не получен path для файла {fileId}.", fileId);
        }

        return result;
    }

    private async ValueTask SendMediaToMatrixAsync(
        MediaInfo mediaInfo,
        string roomKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo.FileId, "mediaInfo.FileId");
        ArgumentNullException.ThrowIfNull(mediaInfo.FileName, "mediaInfo.FileName");
        ArgumentNullException.ThrowIfNull(mediaInfo.MimeType, "mediaInfo.MimeType");
        ArgumentNullException.ThrowIfNull(mediaInfo.MatrixMessageType, "mediaInfo.MatrixMessageType");
        ArgumentNullException.ThrowIfNull(mediaInfo.Caption, "mediaInfo.Caption");
        ArgumentNullException.ThrowIfNull(mediaInfo.FormattedCaption, "mediaInfo.FormattedCaption");

        try
        {
            using var fileStream = await GetMediaFileStreamAsync(mediaInfo.FileId, cancellationToken).ConfigureAwait(false);
            if (fileStream is not null)
            {
                var mxcUri = await _matrixService.UploadFileAsync(fileStream, mediaInfo.FileName, mediaInfo.MimeType, cancellationToken).ConfigureAwait(false);
                if (mxcUri is not null)
                {
                    var messageContent = new Dictionary<string, object>
                    {
                        { "msgtype", mediaInfo.MatrixMessageType },
                        { "filename", mediaInfo.FileName },
                        { "body", mediaInfo.Caption },
                        { "formatted_body", mediaInfo.FormattedCaption },
                        { "format","org.matrix.custom.html" },
                        { "url", mxcUri },
                        {
                            "info", new Dictionary<string, object>
                            {
                                { "mimetype", mediaInfo.MimeType },
                                { "size", mediaInfo.FileSize.GetValueOrDefault(0) },
                            }
                        },
                    };

                    if (mediaInfo.Thumbnail is not null
                        && mediaInfo.Thumbnail.Width is not null
                        && mediaInfo.Thumbnail.Height is not null)
                    {
                        using var thumbFileStream = await GetMediaFileStreamAsync(mediaInfo.Thumbnail.FileId, cancellationToken).ConfigureAwait(false);

                        if (thumbFileStream is not null)
                        {
                            var thumbnailMxcUri = await _matrixService.UploadFileAsync(
                                    thumbFileStream,
                                    mediaInfo.Thumbnail.FileName ?? mediaInfo.FileName,
                                    mediaInfo.Thumbnail.MimeType ?? "image/jpeg",
                                    cancellationToken).ConfigureAwait(false);
                            if (thumbnailMxcUri is not null)
                            {
                                ((Dictionary<string, object>)messageContent["info"]).Add(
                                    "thumbnail_info",
                                    new Dictionary<string, object>
                                    {
                                        { "mimetype", mediaInfo.Thumbnail.MimeType ?? "image/jpeg" },
                                        { "w", mediaInfo.Thumbnail.Width },
                                        { "h", mediaInfo.Thumbnail.Height },
                                        { "size", mediaInfo.Thumbnail.FileSize.GetValueOrDefault(0) },
                                    });
                                ((Dictionary<string, object>)messageContent["info"]).Add("thumbnail_url", thumbnailMxcUri);
                            }
                        }
                    }

                    if (mediaInfo.Width.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add("w", mediaInfo.Width.Value);
                    }

                    if (mediaInfo.Height.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add("h", mediaInfo.Height.Value);
                    }

                    if (mediaInfo.Duration.HasValue)
                    {
                        ((Dictionary<string, object>)messageContent["info"]).Add("duration", mediaInfo.Duration.Value * Program.MillisecondsInOneSecond);
                    }

                    var content = JsonContent.Create(messageContent, MatrixJsonContext.Default.DictionaryStringObject);
                    var response = await _matrixService.SendToRoomAsync(roomKey, content, cancellationToken).ConfigureAwait(false);

                    Log.Information("Медиа отправлено в комнату {roomKey}: {statusCode}", roomKey, response.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка отправки медиа в Matrix.");
        }
    }
}
