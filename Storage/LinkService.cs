using Serilog;

namespace TelegramToMatrixForward.Storage;

/// <summary>
/// Сервис управления связями между Telegram и Matrix.
/// </summary>
internal sealed class LinkService
{
    private readonly EncryptedLinkStore _store;
    private readonly Dictionary<long, string> _telegramIdToMatrixLinks = [];
    private readonly Dictionary<string, (long telegramUserId, DateTimeOffset createdAt)> _pendingCodes = [];
    private readonly TimeProvider _timeProvider;

    public LinkService(TimeProvider timeProvider, ApplicationSettings applicationSettings)
    {
        _store = new EncryptedLinkStore(applicationSettings);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Загружает связи из зашифрованного файла в память при старте.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loadedLinks = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (telegramId, matrixId) in loadedLinks)
        {
            _telegramIdToMatrixLinks[telegramId] = matrixId;
        }

        Log.Information("Загружено {count} связей из файла.", _telegramIdToMatrixLinks.Count);
    }

    /// <summary>
    /// Генерирует уникальный 3-значный код для связывания аккаунта Telegram с Matrix.
    /// </summary>
    /// <param name="telegramUserId">Идентификатор пользователя Telegram.</param>
    /// <returns>Уникальный 3-значный код (например, "472").</returns>
    public string GeneratePendingCode(long telegramUserId)
    {
        string code;
        do
        {
#pragma warning disable MEN010 // 3 цифры кода подтверждения
            code = Random.Shared.Next(1, 1000).ToString("D3");
#pragma warning restore MEN010
        }
        while (_pendingCodes.ContainsKey(code));

        _pendingCodes[code] = (telegramUserId, _timeProvider.GetUtcNow());

        Log.Information("Сгенерирован код {code} для Telegram пользователя.", code);

        return code;
    }

    /// <summary>
    /// Пытается создать связь между аккаунтами Telegram и Matrix по 3-значному коду.
    /// </summary>
    /// <param name="code">3-значный код, полученный от пользователя Matrix.</param>
    /// <returns>true, если связь успешно создана; false, если код не найден или принадлежит другому пользователю.</returns>
    /// <param name="roomKey">Идентификатор комнаты Matrix.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task<bool> TryLinkByCodeAsync(string code, string roomKey, CancellationToken cancellationToken)
    {
        var result = false;
        if (_pendingCodes.TryGetValue(code, out var pendingData))
        {
            var pendingTelegramId = pendingData.telegramUserId;
            _pendingCodes.Remove(code);

            _telegramIdToMatrixLinks[pendingTelegramId] = roomKey;

            await _store.SaveAsync(_telegramIdToMatrixLinks, cancellationToken).ConfigureAwait(false);

            Log.Information("Создана новая связь Telegram -> Matrix.");

            await LoadAsync(cancellationToken).ConfigureAwait(false);
            result = true;
        }

        return result;
    }

    /// <summary>
    /// Получает идентификатор комнаты Matrix для данного пользователя Telegram.
    /// </summary>
    /// <param name="telegramUserId">Идентификатор пользователя Telegram.</param>
    /// <returns>Идентификатор комнаты Matrix или null, если связь не найдена.</returns>
    public string? GetMatrixRoomKey(long telegramUserId)
    {
        return _telegramIdToMatrixLinks.TryGetValue(telegramUserId, out var roomKey) ? roomKey : null;
    }

    /// <summary>
    /// Удаляет связь между аккаунтами Telegram и Matrix.
    /// </summary>
    /// <param name="telegramUserId">Идентификатор пользователя Telegram, связь которого нужно удалить.</param>
    /// <returns>true, если связь была найдена и удалена; false, если связь не найдена.</returns>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task UnlinkAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        _telegramIdToMatrixLinks.Remove(telegramUserId);
        await _store.SaveAsync(_telegramIdToMatrixLinks, cancellationToken).ConfigureAwait(false);
        Log.Information("Связь удалена для пользователя.");
    }

    /// <summary>
    /// Получает идентификатор пользователя Telegram для данного пользователя Matrix.
    /// </summary>
    /// <param name="matrixUserId">Идентификатор пользователя Matrix (например, "@user:matrix.org").</param>
    /// <returns>Идентификатор пользователя Telegram или null, если связь не найдена.</returns>
    public long? GetTelegramUserId(string matrixUserId)
    {
        return _telegramIdToMatrixLinks.FirstOrDefault(kvp => kvp.Value == matrixUserId).Key;
    }
}
