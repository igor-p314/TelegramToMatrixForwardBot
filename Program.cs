namespace TelegramToMatrixForward;

using Serilog;
using System.Threading;
using System.Threading.Tasks;
using TelegramToMatrixForward.Matrix;
using TelegramToMatrixForward.Storage;
using TelegramToMatrixForward.Telegram;

/// <summary>
/// Точка входа приложения.
/// </summary>
public class Program
{
    internal const int MillisecondsInOneSecond = 1000;

    private const long OneDayInMilliseconds = 86_400_000L;

    private static readonly CancellationTokenSource CancelTokenSource = new();

    public static int PollTimeoutMilliseconds { get; private set; }

    public static async Task Main()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File("logs/log.txt", rollingInterval: RollingInterval.Day)
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u4} {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                ?? throw new InvalidOperationException("Не задана переменная среды TELEGRAM_BOT_TOKEN");

            var encryptionKey = Environment.GetEnvironmentVariable("LINKS_ENCRYPTION_KEY")
                ?? throw new InvalidOperationException("Не задана переменная среды LINKS_ENCRYPTION_KEY");

            var linksFilePath = Environment.GetEnvironmentVariable("LINKS_FILE_PATH") ?? "data/links.bin";

            var maxFileSizeMb = int.Parse(Environment.GetEnvironmentVariable("MAX_FILE_SIZE_MB") ?? "50");

            PollTimeoutMilliseconds = int.Parse(Environment.GetEnvironmentVariable("BOT_POLL_TIMEOUT") ?? "30000");

            var batchTokenPath = Environment.GetEnvironmentVariable("MATRIX_BOT_BATCH_TOKEN_PATH") ?? "data/token.txt";
            var messageRetention = long.TryParse(Environment.GetEnvironmentVariable("MATRIX_ROOM_RETENTION"), out var parsed)
                                                        ? parsed
                                                        : OneDayInMilliseconds;

            var offsetIdPath = Environment.GetEnvironmentVariable("TELEGRAM_BOT_OFFSETID_PATH") ?? "data/offset.txt";

            var linkService = new LinkService(TimeProvider.System, linksFilePath, encryptionKey);
            await linkService.LoadAsync(CancelTokenSource.Token).ConfigureAwait(false);

            var matrixService = new MatrixService(linkService, batchTokenPath, messageRetention);

            var telegramApiService = new TelegramApiService(botToken);
            var telegramService = new TelegramService(telegramApiService, matrixService, linkService, maxFileSizeMb, offsetIdPath);

            IEnumerable<Task> syncTasks = [
                matrixService.StartAsync(CancelTokenSource.Token),
                telegramService.StartAsync(CancelTokenSource.Token)];

            Log.Information("Бот запущен. Ожидание сообщений...");

            var result = await Task.WhenAny(syncTasks).ConfigureAwait(false);
            if (result.Exception is not null)
            {
                throw result.Exception;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Бот остановлен.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Бот завершился с ошибкой.");
        }
        finally
        {
            CancelTokenSource.Dispose();
            Log.CloseAndFlush();
        }
    }
}
