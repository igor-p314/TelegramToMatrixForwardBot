using Polly;
using Polly.Retry;
using Serilog;
using System.Net;

namespace TelegramToMatrixForward.Matrix;

/// <summary>
/// HTTP-клиент для Matrix API с retry политикой.
/// </summary>
internal sealed class MatrixApiService
{
    private const int AdditionalTimeoutMilliseconds = 5000;
    private const string MatrixPrefix = "matrix";
    private readonly HttpClient _matrixHttpClient;

    private ResiliencePipeline<HttpResponseMessage>? _resiliencePipeline;
    private ResiliencePipeline<Stream>? _streamResiliencePipeline;

    internal string HomeServerUrl { get; }

    /// <summary>
    /// Создаёт экземпляр HTTP-клиента для Matrix API.
    /// </summary>
    /// <param name="applicationSettings">Настройки приложения.</param>
    public MatrixApiService(ApplicationSettings applicationSettings)
    {
        HomeServerUrl = Environment.GetEnvironmentVariable("MATRIX_HOMESERVER_URL")
            ?? throw new InvalidOperationException("Не задана переменная среды MATRIX_HOMESERVER_URL");

        _matrixHttpClient = new HttpClient()
        {
            Timeout = TimeSpan.FromMilliseconds(applicationSettings.PollTimeoutMilliseconds + AdditionalTimeoutMilliseconds),
            BaseAddress = new Uri($"https://{MatrixPrefix}.{HomeServerUrl}"),
        };
    }

    /// <summary>
    /// Авторизует бота в Matrix через API.
    /// </summary>
    /// <param name="url">URL для авторизации.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Сервис авторизации с полученным токеном.</returns>
    internal async Task<AuthorizationService> AuthorizeAsync(string url, CancellationToken cancellationToken)
    {
        var authorizationService = new AuthorizationService(this);

        string bearer = await authorizationService.AuthorizeAsync(url, cancellationToken).ConfigureAwait(false);
        _matrixHttpClient.DefaultRequestHeaders.Clear();
        _matrixHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearer}");

        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 1,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode == HttpStatusCode.Forbidden || r.StatusCode == HttpStatusCode.Unauthorized),
                OnRetry = async args =>
                {
                    Log.Information("Authorization error. Re-authorizing.");
                    bearer = await authorizationService.AuthorizeAsync(url, args.Context.CancellationToken).ConfigureAwait(false);
                    _matrixHttpClient.DefaultRequestHeaders.Clear();
                    _matrixHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearer}");
                },
            }).Build();

        _streamResiliencePipeline = new ResiliencePipelineBuilder<Stream>()
            .AddRetry(new RetryStrategyOptions<Stream>
            {
                MaxRetryAttempts = 1,
                ShouldHandle = new PredicateBuilder<Stream>()
                    .HandleResult(r => r == null),
            }).Build();

        return authorizationService;
    }

    /// <summary>
    /// Выполняет POST-запрос с retry политикой.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="content">Тело запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Ответ сервера.</returns>
    internal async Task<HttpResponseMessage> PostAsync(string url, HttpContent? content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_resiliencePipeline, "MatrixService not initialized.");

        var response = await _resiliencePipeline.ExecuteAsync(
            async token =>
            {
                var resp = await _matrixHttpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
                return resp;
            },
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Выполняет POST-запрос без retry политики.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="content">Тело запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Ответ сервера.</returns>
    internal async Task<HttpResponseMessage> PostNoRetryAsync(string url, HttpContent content, CancellationToken cancellationToken)
    {
        var response = await _matrixHttpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Выполняет PUT-запрос с retry политикой.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="content">Тело запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Ответ сервера.</returns>
    internal async Task<HttpResponseMessage> PutAsync(string url, HttpContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_resiliencePipeline, "MatrixService not initialized.");

        var response = await _resiliencePipeline.ExecuteAsync(
            async token =>
            {
                var resp = await _matrixHttpClient.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
                return resp;
            },
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Выполняет GET-запрос с retry политикой.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Ответ сервера.</returns>
    internal async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_resiliencePipeline, "MatrixService not initialized.");
        await HealthService.HeartBeatMatrixAsync(cancellationToken).ConfigureAwait(false);

        var response = await _resiliencePipeline.ExecuteAsync(
            async token =>
            {
                var resp = await _matrixHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                return resp;
            },
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Выполняет GET-запрос и возвращает тело ответа как строку.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Тело ответа в виде строки.</returns>
    internal async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет GET-запрос и возвращает тело ответа как поток.
    /// </summary>
    /// <param name="url">Относительный URL запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Поток с данными ответа.</returns>
    internal async Task<Stream> GetStreamAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_streamResiliencePipeline, "MatrixService not initialized.");

        var response = await _streamResiliencePipeline.ExecuteAsync(
            async (ctx, token) =>
            {
                var resp = await _matrixHttpClient.GetAsync(url, token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return response;
    }
}
