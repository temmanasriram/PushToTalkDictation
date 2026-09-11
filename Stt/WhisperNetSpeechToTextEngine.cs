using Microsoft.Extensions.Logging;
using PushToTalkDictation.Audio;
using PushToTalkDictation.Configuration;
using Whisper.net;

namespace PushToTalkDictation.Stt;

/// <summary>
/// whisper.cpp through the Whisper.net bindings. Takes any GGML file —
/// ggml-base.en, a quantised ggml-*-q5_1, or a distil-whisper GGML conversion.
///
/// Note on latency: whisper.cpp pads every request to a 30-second mel window, so a
/// one-second utterance costs roughly what a thirty-second one does. That is the
/// main reason the sherpa-onnx/Moonshine engine is the default for push-to-talk.
/// </summary>
public sealed class WhisperNetSpeechToTextEngine : SpeechToTextEngineBase
{
    private readonly WhisperNetSettings _settings;
    private readonly string? _modelRoot;
    private readonly ILogger<WhisperNetSpeechToTextEngine> _logger;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public override string Name => $"whisper.cpp ({Path.GetFileName(_settings.ModelPath)})";

    public WhisperNetSpeechToTextEngine(AppSettings settings, ILogger<WhisperNetSpeechToTextEngine> logger)
    {
        _settings = settings.SpeechToText.WhisperNet;
        _modelRoot = settings.SpeechToText.ModelRoot;
        _logger = logger;
    }

    protected override Task LoadAsync(CancellationToken ct) => Task.Run(() =>
    {
        var path = ModelPathResolver.ResolveFile(_settings.ModelPath, _modelRoot);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _factory = WhisperFactory.FromPath(path);

        var builder = _factory.CreateBuilder()
            .WithLanguage(_settings.Language)
            .WithThreads(Math.Max(1, _settings.NumThreads))
            .WithTemperature(0f)
            // Dictation clips are independent utterances; carrying context between
            // them is what produces runaway repetition on short input.
            .WithNoContext();

        _processor = builder.Build();

        _logger.LogInformation("Loaded GGML model '{Path}' in {Ms} ms.", path, sw.ElapsedMilliseconds);
    }, ct);

    protected override async Task<string> RecognizeAsync(AudioClip clip, CancellationToken ct)
    {
        var processor = _processor ?? throw new InvalidOperationException("Processor not initialised.");

        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var builder = new System.Text.StringBuilder();

            await foreach (var segment in processor.ProcessAsync(clip.Samples, ct).ConfigureAwait(false))
                builder.Append(segment.Text);

            _logger.LogDebug("Decoded {Audio:F2}s of audio in {Ms} ms.",
                clip.Duration.TotalSeconds, sw.ElapsedMilliseconds);

            return builder.ToString();
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync().ConfigureAwait(false);
        _factory?.Dispose();
        _processor = null;
        _factory = null;
        _decodeGate.Dispose();
    }
}
