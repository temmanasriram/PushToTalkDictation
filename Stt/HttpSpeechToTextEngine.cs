using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Audio;
using PushToTalkDictation.Configuration;

namespace PushToTalkDictation.Stt;

/// <summary>
/// Posts the clip as a WAV to a local OpenAI-compatible transcription endpoint.
/// Works unmodified against faster-whisper-server / Speaches, whisper.cpp's
/// <c>server</c> example, and any wrapper that returns <c>{"text": "..."}</c>.
///
/// Keeps native dependencies out of the C# process entirely — useful when you want
/// the model on a GPU box, or want to iterate on the Python side without rebuilding.
/// </summary>
public sealed class HttpSpeechToTextEngine : SpeechToTextEngineBase
{
    private readonly HttpSttSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpSpeechToTextEngine> _logger;

    public const string HttpClientName = "stt";

    public override string Name => $"HTTP ({_settings.Model})";

    public HttpSpeechToTextEngine(
        AppSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpSpeechToTextEngine> logger)
    {
        _settings = settings.SpeechToText.Http;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override Task LoadAsync(CancellationToken ct)
    {
        // Nothing to load locally. The remote model warms itself on first request.
        _logger.LogInformation("HTTP STT engine targeting {Endpoint}.", _settings.Endpoint);
        return Task.CompletedTask;
    }

    protected override async Task<string> RecognizeAsync(AudioClip clip, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds));

        using var content = new MultipartFormDataContent();

        var wav = new ByteArrayContent(clip.ToWavBytes());
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "audio.wav");
        content.Add(new StringContent(_settings.Model), "model");
        content.Add(new StringContent(_settings.Language), "language");
        content.Add(new StringContent("json"), "response_format");
        content.Add(new StringContent("0"), "temperature");

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint) { Content = content };
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("STT endpoint returned {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            return string.Empty;
        }

        _logger.LogDebug("Transcribed {Audio:F2}s of audio in {Ms} ms.",
            clip.Duration.TotalSeconds, sw.ElapsedMilliseconds);

        return ExtractText(body);
    }

    private static string ExtractText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Some servers reply with bare text.
        }

        return body;
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "...";
}
