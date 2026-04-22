using Serilog;
using System.Threading.Channels;
using TelegramToMatrixForward.Dto.Telegram;
using TelegramToMatrixForward.Storage;

namespace TelegramToMatrixForward.Telegram;

/// <summary>
/// Сервис обработки сообщений Telegram и пересылки в Matrix.
/// </summary>
internal sealed class TelegramService
{
    private const int MaxVideoHeight = 720;

    private readonly ApplicationSettings _applicationSettings;

    private readonly TelegramApiService _apiService;

    private readonly ChannelWriter<Message> _writeChannel;
    private readonly ChannelReader<Dto.Matrix.ToTelegramMessage> _readChannel;

    private readonly LinkService _linkService;

    private readonly int _maxFileSizeInBytes;
    private readonly int _maxFileSizeMegaBytes;

    /// <summary>
    /// Создаёт экземпляр сервиса Telegram.
    /// </summary>
    /// <param name="linkService">Сервис управления связями.</param>
    /// <param name="applicationSettings">Настройки приложения.</param>
    public TelegramService(
        LinkService linkService,
        ApplicationSettings applicationSettings)
    {
        _apiService = new TelegramApiService(applicationSettings);
        _writeChannel = Program.ToMatrixChannel.Writer;
        _readChannel = Program.ToTelegramChannel.Reader;

        _linkService = linkService;
        _applicationSettings = applicationSettings;
        _maxFileSizeMegaBytes = applicationSettings.MaxFileSizeMb;
#pragma warning disable MEN010 // Байты килобайты
        _maxFileSizeInBytes = applicationSettings.MaxFileSizeMb * 1024 * 1024;
#pragma warning restore MEN010
    }

    /// <summary>
    /// Обрабатывает одно сообщение из Telegram.
    /// </summary>
    /// <param name="message">Сообщение из Telegram.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task ProcessMessageAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message.From, "message.From");
        switch (message.Text)
        {
            case string text when "/start".Equals(text?.Trim()):
                await HandleStartCommandAsync(message.From.Id, cancellationToken).ConfigureAwait(false);
                break;
            case string text when "/stop".Equals(text?.Trim()):
                await HandleStopCommandAsync(message.From.Id, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await SendMessageToMatrixAsync(message, cancellationToken).ConfigureAwait(false);
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

        if (!string.IsNullOrEmpty(_applicationSettings.TelegramOffsetIdPath) && File.Exists(_applicationSettings.TelegramOffsetIdPath))
        {
            offset = int.Parse(await File.ReadAllTextAsync(_applicationSettings.TelegramOffsetIdPath, cancellationToken).ConfigureAwait(false));
        }

        _ = ListenMatrixToTelegramChannelAsync(_readChannel, _apiService,  cancellationToken); // fire and forget

        Log.Information("Telegram bot запущен в режиме long polling.");

        Program.TelegramServiceReady.SetResult(true);
        await Program.MatrixServiceReady.Task.ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _apiService.GetUpdatesAsync(offset, cancellationToken).ConfigureAwait(false);

                foreach (var update in updates)
                {
                    if (update.Message is not null && update.Message.From is not null)
                    {
                        await ProcessMessageAsync(update.Message, cancellationToken).ConfigureAwait(false);

                        offset = update.UpdateId + 1;
                        await SaveOffsetAsync(offset.ToString(), cancellationToken).ConfigureAwait(false);
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

    private static async Task ListenMatrixToTelegramChannelAsync(
        ChannelReader<Dto.Matrix.ToTelegramMessage> reader,
        TelegramApiService apiService,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await apiService.SendMessageAsync(message.ChatId, message.Text, cancellationToken).ConfigureAwait(false);
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

    private static ProcessedMedia SetPhotoDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "image/jpeg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "photo.jpg";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.image",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetDocumentDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "application/octet-stream";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "document";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.file",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetVideoDefaults(MediaInfo mediaInfo, long maxFileSizeInBytes)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "video/mp4";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "video.mp4";
        }

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

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.video",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetAudioDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "audio/mpeg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "audio.mp3";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.audio",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetVoiceDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "audio/ogg";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "voice.ogg";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.audio",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetVideoNoteDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = "video/mp4";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = "videonote.mp4";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = "m.video",
            MediaInfo = mediaInfo,
        };
    }

    private static ProcessedMedia SetStickerDefaults(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.MimeType))
        {
            mediaInfo.MimeType = mediaInfo.IsVideo.GetValueOrDefault() ? "video/webm" : "image/webp";
        }

        if (string.IsNullOrEmpty(mediaInfo.FileName))
        {
            mediaInfo.FileName = mediaInfo.IsVideo.GetValueOrDefault() ? "sticker.webm" : "sticker.webp";
        }

        return new ProcessedMedia()
        {
            MatrixMessageType = mediaInfo.IsVideo.GetValueOrDefault() ? "m.video" : "m.image",
            MediaInfo = mediaInfo,
        };
    }

    private async Task HandleStartCommandAsync(long telegramUserId, CancellationToken cancellationToken)
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

    private async Task HandleStopCommandAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        await _linkService.UnlinkAsync(telegramUserId, cancellationToken).ConfigureAwait(false);
        await _apiService.SendMessageAsync(telegramUserId, "✅ Связь с Matrix удалена.", cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveOffsetAsync(string offset, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_applicationSettings.TelegramOffsetIdPath))
        {
            await File.WriteAllTextAsync(_applicationSettings.TelegramOffsetIdPath, offset, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Log.Information("Не задан путь к Telegram Offset");
        }
    }

    private async Task SendMessageToMatrixAsync(Message message, CancellationToken cancellationToken)
    {
        var mustSend = true;
        var mediaSize = message.GetMediaSize(_maxFileSizeInBytes);
        if (mediaSize > 0)
        {
            mustSend = mediaSize <= _maxFileSizeInBytes;
            if (mustSend)
            {
                message.ProcessedMedia = await ProcessMediaAsync(message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _apiService.SendMessageAsync(
                        message.From!.Id,
                        $"❌ Размер пересылаемого медиа превышает установленный лимит в {_maxFileSizeMegaBytes} мбайт.",
                        cancellationToken).ConfigureAwait(false);
            }
        }

        if (mustSend)
        {
            await _writeChannel.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessedMedia> ProcessMediaAsync(Message message, CancellationToken cancellationToken)
    {
        ProcessedMedia processedMedia = message switch
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

        await DownloadMediaFilesAsync(
                    processedMedia,
                    cancellationToken).ConfigureAwait(false);

        return processedMedia;
    }

    private async Task<ProcessedMedia> DownloadMediaFilesAsync(
        ProcessedMedia processedMedia,
        CancellationToken cancellationToken)
    {
        processedMedia.MediaFileStream = await _apiService.GetMediaFileStreamAsync(processedMedia.MediaInfo.FileId, cancellationToken).ConfigureAwait(false);
        if (processedMedia.MediaInfo.Thumbnail is not null)
        {
            processedMedia.ThumbnailFileStream = await _apiService.GetMediaFileStreamAsync(processedMedia.MediaInfo.Thumbnail.FileId, cancellationToken)
                .ConfigureAwait(false);
        }

        return processedMedia;
    }
}
