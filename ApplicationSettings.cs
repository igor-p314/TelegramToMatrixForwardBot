namespace TelegramToMatrixForward;

internal struct ApplicationSettings
{
    internal const int MillisecondsInOneSecond = 1000;

    private const long OneDayInMilliseconds = 86_400_000L;

    public string TelegramBotToken { get; }

    public string EncryptionKey { get; }

    public string LinksFilePath { get; }

    public int MaxFileSizeMb { get; }

    public int PollTimeoutMilliseconds { get; }

    public string MatrixBatchTokenPath { get; }

    public long MessageRetentionPolicyInMilliseconds { get; }

    public string TelegramOffsetIdPath { get; }

    public string TelegramApiUrl { get; }

    public bool TelegramLocalFilesMode { get; } = false;

    public ApplicationSettings()
    {
        TelegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new InvalidOperationException("Не задана переменная среды TELEGRAM_BOT_TOKEN");

        EncryptionKey = Environment.GetEnvironmentVariable("LINKS_ENCRYPTION_KEY")
            ?? throw new InvalidOperationException("Не задана переменная среды LINKS_ENCRYPTION_KEY");

        LinksFilePath = Environment.GetEnvironmentVariable("LINKS_FILE_PATH") ?? "data/links.bin";

        MaxFileSizeMb = int.Parse(Environment.GetEnvironmentVariable("MAX_FILE_SIZE_MB") ?? "50");

        PollTimeoutMilliseconds = int.Parse(Environment.GetEnvironmentVariable("BOT_POLL_TIMEOUT") ?? "30000");

        MatrixBatchTokenPath = Environment.GetEnvironmentVariable("MATRIX_BOT_BATCH_TOKEN_PATH") ?? "data/token.txt";
        MessageRetentionPolicyInMilliseconds = long.TryParse(Environment.GetEnvironmentVariable("MATRIX_ROOM_RETENTION"), out var parsed)
                                                    ? parsed
                                                    : OneDayInMilliseconds;

        TelegramOffsetIdPath = Environment.GetEnvironmentVariable("TELEGRAM_BOT_OFFSETID_PATH") ?? "data/offset.txt";
        string? url = Environment.GetEnvironmentVariable("TELEGRAM_API_URL");
        if (string.IsNullOrEmpty(url))
        {
            TelegramApiUrl = "https://api.telegram.org/";
        }
        else
        {
            TelegramLocalFilesMode = true;
            TelegramApiUrl = url;
        }
    }
}
