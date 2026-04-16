namespace TelegramToMatrixForward;

internal static class HealthService
{
    internal static Task HeartBeatMatrixAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        return Task.CompletedTask;
#else
        return File.WriteAllTextAsync("/tmp/matrix.hrtbt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), cancellationToken);
#endif
    }

    internal static Task HeartBeatTelegramAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        return Task.CompletedTask;
#else
        return File.WriteAllTextAsync("/tmp/tg.hrtbt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), cancellationToken);
#endif
    }
}
