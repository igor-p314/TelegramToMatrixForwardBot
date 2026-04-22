namespace TelegramToMatrixForward;

using Serilog;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TelegramToMatrixForward.Dto.Matrix;
using TelegramToMatrixForward.Matrix;
using TelegramToMatrixForward.Storage;
using TelegramToMatrixForward.Telegram;

/// <summary>
/// Точка входа приложения.
/// </summary>
public class Program
{
    internal static readonly TaskCompletionSource<bool> TelegramServiceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static readonly TaskCompletionSource<bool> MatrixServiceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static readonly Channel<Dto.Telegram.Message> ToMatrixChannel = Channel.CreateUnbounded<Dto.Telegram.Message>();
    internal static readonly Channel<ToTelegramMessage> ToTelegramChannel = Channel.CreateUnbounded<ToTelegramMessage>();

    private static readonly CancellationTokenSource CancelTokenSource = new();

    public static async Task Main()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File("logs/log.txt", rollingInterval: RollingInterval.Day)
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u4} {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var applicationSettings = new ApplicationSettings();
        try
        {
            var linkService = new LinkService(TimeProvider.System, applicationSettings);
            await linkService.LoadAsync(CancelTokenSource.Token).ConfigureAwait(false);

            var matrixService = new MatrixService(linkService, applicationSettings);

            var telegramService = new TelegramService(linkService, applicationSettings);

            IEnumerable<Task> syncTasks = [
                matrixService.StartAsync(CancelTokenSource.Token),
                telegramService.StartAsync(CancelTokenSource.Token)];

            Log.Information("Боты запущены. Ожидание сообщений...");

            var result = await Task.WhenAny(syncTasks).ConfigureAwait(false);
            if (result.Exception is not null)
            {
                throw result.Exception;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Боты остановлены.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Ошибка в процессе выполнения.");
        }
        finally
        {
            CancelTokenSource.Dispose();
            Log.CloseAndFlush();
        }
    }
}
