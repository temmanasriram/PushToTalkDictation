using Microsoft.Extensions.Logging;
using PushToTalkDictation.Audio;
using PushToTalkDictation.Configuration;
using SherpaOnnx;

namespace PushToTalkDictation.Stt;

/// <summary>
/// Default engine. Wraps sherpa-onnx's <c>OfflineRecognizer</c>, which runs ONNX
/// Runtime locally with no network and no Python.
///
/// The same class covers four model families, selected by <c>ModelKind</c>:
///   Moonshine    - UsefulSensors Moonshine (Base/Tiny, English). Compute scales with
///                  the real length of the clip, so a 2-second utterance costs 2 seconds
///                  of audio rather than Whisper's fixed 30-second window. Best latency
///                  for push-to-talk.
///   Transducer   - NVIDIA Parakeet TDT / Zipformer. Higher accuracy, more RAM.
///   Whisper      - OpenAI / distil-whisper exported to ONNX.
///   SenseVoice   - multilingual, very fast, punctuation-aware.
/// </summary>
public sealed class SherpaOnnxSpeechToTextEngine : SpeechToTextEngineBase
{
    private readonly SherpaOnnxSettings _settings;
    private readonly string? _modelRoot;
    private readonly ILogger<SherpaOnnxSpeechToTextEngine> _logger;

    // No decode gate here: SpeechToTextEngineBase serialises load, decode and unload
    // against one another, so the native session is never driven by two decodes at once
    // and can never be freed mid-decode.
    private OfflineRecognizer? _recognizer;

    public override string Name => $"sherpa-onnx ({_settings.ModelKind})";

    public SherpaOnnxSpeechToTextEngine(AppSettings settings, ILogger<SherpaOnnxSpeechToTextEngine> logger)
    {
        _settings = settings.SpeechToText.SherpaOnnx;
        _modelRoot = settings.SpeechToText.ModelRoot;
        _logger = logger;
    }

    protected override Task LoadAsync(CancellationToken ct) => Task.Run(() =>
    {
        // Probed rather than resolved straight off the executable directory, so the same
        // config works from a dev build, a published folder and a per-user model store.
        var dir = ModelPathResolver.ResolveDirectory(_settings.ModelDirectory, _modelRoot);

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Tokens = Require(dir, _settings.Tokens);
        config.ModelConfig.NumThreads = Math.Max(1, _settings.NumThreads);
        config.ModelConfig.Provider = _settings.Provider;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = _settings.DecodingMethod;

        switch (_settings.ModelKind)
        {
            case SherpaModelKind.Moonshine:
                config.ModelConfig.Moonshine.Preprocessor = Require(dir, _settings.MoonshinePreprocessor);
                config.ModelConfig.Moonshine.Encoder = Require(dir, _settings.MoonshineEncoder);
                config.ModelConfig.Moonshine.UncachedDecoder = Require(dir, _settings.MoonshineUncachedDecoder);
                config.ModelConfig.Moonshine.CachedDecoder = Require(dir, _settings.MoonshineCachedDecoder);
                break;

            case SherpaModelKind.Transducer:
                config.ModelConfig.Transducer.Encoder = Require(dir, _settings.TransducerEncoder);
                config.ModelConfig.Transducer.Decoder = Require(dir, _settings.TransducerDecoder);
                config.ModelConfig.Transducer.Joiner = Require(dir, _settings.TransducerJoiner);
                config.ModelConfig.ModelType = _settings.TransducerModelType;
                break;

            case SherpaModelKind.Whisper:
                config.ModelConfig.Whisper.Encoder = Require(dir, _settings.WhisperEncoder);
                config.ModelConfig.Whisper.Decoder = Require(dir, _settings.WhisperDecoder);
                config.ModelConfig.Whisper.Language = "en";
                config.ModelConfig.Whisper.Task = "transcribe";
                break;

            case SherpaModelKind.SenseVoice:
                config.ModelConfig.SenseVoice.Model = Require(dir, _settings.SenseVoiceModel);
                config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                break;

            default:
                throw new NotSupportedException($"Unknown sherpa model kind '{_settings.ModelKind}'.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // LoadAsync runs again after an unload, so never leak a previous session.
        _recognizer?.Dispose();
        _recognizer = new OfflineRecognizer(config);

        _logger.LogInformation("Loaded {Kind} model from '{Dir}' in {Ms} ms.",
            _settings.ModelKind, dir, sw.ElapsedMilliseconds);
    }, ct);

    protected override async Task<string> RecognizeAsync(AudioClip clip, CancellationToken ct)
    {
        var recognizer = _recognizer ?? throw new InvalidOperationException("Recognizer not initialised.");

        return await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(clip.SampleRate, clip.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text;

            _logger.LogDebug("Decoded {Audio:F2}s of audio in {Ms} ms (RTF {Rtf:F2}).",
                clip.Duration.TotalSeconds, sw.ElapsedMilliseconds,
                sw.Elapsed.TotalSeconds / Math.Max(0.001, clip.Duration.TotalSeconds));

            return text;
        }, ct).ConfigureAwait(false);
    }

    private static string Require(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file '{fileName}' is missing from '{dir}'.", path);
        return path;
    }

    protected override ValueTask UnloadCoreAsync()
    {
        if (_recognizer is null) return ValueTask.CompletedTask;

        _recognizer.Dispose();
        _recognizer = null;
        _logger.LogInformation("Released the {Kind} model.", _settings.ModelKind);

        return ValueTask.CompletedTask;
    }
}
