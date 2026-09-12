using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PushToTalkDictation.Configuration;

namespace PushToTalkDictation.Audio;

public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    /// <summary>Opens the capture device and starts filling the in-memory buffer.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops capture and returns the clip resampled to the configured rate, mono.</summary>
    Task<AudioClip> StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the audio buffered so far and keeps recording, for transcribing a long
    /// dictation in segments. The cut is nudged to the quietest moment near the end so a
    /// word is not split down the middle; whatever follows that point is carried into the
    /// next segment. Returns an empty clip when not recording, or when there is less than
    /// <paramref name="minMs"/> of audio to hand over.
    /// </summary>
    Task<AudioClip> FlushAsync(int minMs, CancellationToken ct = default);

    /// <summary>Stops capture and throws the buffer away.</summary>
    Task CancelAsync();
}

/// <summary>
/// WASAPI shared-mode capture via NAudio.
///
/// The device hands us its own mix format (typically 32-bit float, 48 kHz, stereo).
/// On the capture thread we only downmix to mono — cheap, no allocation per buffer
/// beyond the growing list. The sample-rate conversion to 16 kHz runs once at Stop,
/// on the thread pool, which costs a millisecond or two for a normal utterance and
/// keeps the audio callback free of work.
/// </summary>
public sealed class AudioRecorder : IAudioRecorder
{
    private readonly AudioSettings _settings;
    private readonly ILogger<AudioRecorder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WasapiCapture? _capture;

    /// <summary>
    /// Guards <see cref="_monoBuffer"/>. The capture callback appends to it while a flush
    /// may be detaching it, so both sides take this. The locked regions are deliberately
    /// tiny - an append of one 50 ms buffer, or a reference swap - and this is the audio
    /// callback rather than the keyboard hook, so brief contention here is harmless.
    /// </summary>
    private readonly object _bufferLock = new();

    private List<float>? _monoBuffer;

    /// <summary>
    /// Audio after the last segment's cut point, waiting to be prepended to the next one.
    /// Only ever touched by the flush/stop path, never by the capture callback.
    /// </summary>
    private float[] _carryOver = [];

    private int _nativeSampleRate;
    private TaskCompletionSource? _stopped;
    private bool _disposed;

    public bool IsRecording { get; private set; }

    public AudioRecorder(AppSettings settings, ILogger<AudioRecorder> logger)
    {
        _settings = settings.Audio;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRecording) return;

            var device = ResolveDevice();
            // 50 ms buffer: short enough that the tail after key-release is negligible.
            var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);

            _nativeSampleRate = capture.WaveFormat.SampleRate;

            // ~4 s preallocated; List growth after that is amortised and off the hot path.
            _monoBuffer = new List<float>(_nativeSampleRate * 4);
            _carryOver = [];
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            _capture = capture;
            IsRecording = true;

            capture.StartRecording();

            _logger.LogDebug("Capture started on '{Device}' at {Rate} Hz / {Channels} ch / {Encoding}.",
                device.FriendlyName, capture.WaveFormat.SampleRate, capture.WaveFormat.Channels,
                capture.WaveFormat.Encoding);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioClip> StopAsync(CancellationToken ct = default)
    {
        var (samples, rate) = await StopCoreAsync().ConfigureAwait(false);
        if (samples is null || samples.Length == 0)
            return AudioClip.Empty(_settings.TargetSampleRate);

        // Resample off the caller's path.
        return await Task.Run(() =>
        {
            var resampled = Resample(samples, rate, _settings.TargetSampleRate);
            return new AudioClip(resampled, _settings.TargetSampleRate);
        }, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

    public async Task<AudioClip> FlushAsync(int minMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        float[] pending;
        int rate;
        try
        {
            if (!IsRecording) return AudioClip.Empty(_settings.TargetSampleRate);

            rate = _nativeSampleRate;
            pending = TakeBufferedAudio();

            var minSamples = minMs * rate / 1000;
            if (pending.Length < minSamples)
            {
                // Too little to be worth a segment - hand it back to the next one.
                _carryOver = pending;
                return AudioClip.Empty(_settings.TargetSampleRate);
            }

            // Leave the tail after the quietest nearby moment for the next segment, so the
            // cut lands in a gap between words rather than through one.
            var split = FindQuietSplit(pending, rate);
            _carryOver = pending[split..];
            pending = pending[..split];
        }
        finally
        {
            _gate.Release();
        }

        if (pending.Length == 0) return AudioClip.Empty(_settings.TargetSampleRate);

        return await Task.Run(() =>
            new AudioClip(Resample(pending, rate, _settings.TargetSampleRate), _settings.TargetSampleRate),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches everything buffered so far, prepending any carry-over from the previous
    /// segment, and installs a fresh buffer for the capture callback to keep filling.
    /// The lock is held only for the swap.
    /// </summary>
    private float[] TakeBufferedAudio()
    {
        List<float>? detached;
        lock (_bufferLock)
        {
            detached = _monoBuffer;
            if (detached is not null)
                _monoBuffer = new List<float>(Math.Max(1024, detached.Capacity));
        }

        var carried = _carryOver;
        _carryOver = [];

        if (detached is null || detached.Count == 0) return carried;
        if (carried.Length == 0) return detached.ToArray();

        var combined = new float[carried.Length + detached.Count];
        carried.CopyTo(combined, 0);
        detached.CopyTo(combined, carried.Length);
        return combined;
    }

    /// <summary>
    /// Picks a cut point near the end of <paramref name="samples"/> at the quietest short
    /// window it can find, which is very likely a pause between words. Falls back to the
    /// very end when the audio is too short to search.
    /// </summary>
    private static int FindQuietSplit(float[] samples, int sampleRate)
    {
        const int searchMs = 1500;   // how far back from the end to look
        const int windowMs = 60;     // width of the quiet window we are hunting for

        var window = Math.Max(1, windowMs * sampleRate / 1000);
        var searchSpan = Math.Max(window, searchMs * sampleRate / 1000);

        var from = samples.Length - searchSpan;
        if (from < window) return samples.Length;   // not enough audio to be choosy

        // Sliding sum of squares: one pass over the search span.
        double sum = 0;
        for (var i = from; i < from + window; i++) sum += (double)samples[i] * samples[i];

        var bestSum = sum;
        var bestStart = from;

        for (var i = from + window; i < samples.Length; i++)
        {
            sum += (double)samples[i] * samples[i];
            sum -= (double)samples[i - window] * samples[i - window];

            if (sum < bestSum)
            {
                bestSum = sum;
                bestStart = i - window + 1;
            }
        }

        // Cut in the middle of the quiet window.
        return Math.Clamp(bestStart + window / 2, 1, samples.Length);
    }

    private async Task<(float[]? Samples, int Rate)> StopCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        WasapiCapture? capture;
        Task? stopped;
        try
        {
            if (!IsRecording) return (null, 0);
            IsRecording = false;
            capture = _capture;
            stopped = _stopped?.Task;
            capture?.StopRecording();
        }
        finally
        {
            _gate.Release();
        }

        // WASAPI flushes the last buffers asynchronously; wait briefly so the tail
        // of the utterance is not clipped.
        if (stopped is not null)
            await Task.WhenAny(stopped, Task.Delay(500)).ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Includes anything held back by the last segment's cut.
            var samples = TakeBufferedAudio();
            var rate = _nativeSampleRate;

            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
            }

            _capture = null;
            lock (_bufferLock) _monoBuffer = null;
            _carryOver = [];
            _stopped = null;

            return (samples, rate);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------- capture path

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = _monoBuffer;
        var format = _capture?.WaveFormat;
        if (buffer is null || format is null || e.BytesRecorded == 0) return;

        var channels = format.Channels;
        var data = e.Buffer.AsSpan(0, e.BytesRecorded);

        lock (_bufferLock)
        {
            // Re-check: a flush may have swapped the buffer since we read the field.
            if (!ReferenceEquals(buffer, _monoBuffer)) buffer = _monoBuffer;
            if (buffer is null) return;

            switch (format.Encoding, format.BitsPerSample)
            {
                case (WaveFormatEncoding.IeeeFloat, 32):
                case (WaveFormatEncoding.Extensible, 32):
                    AppendFloat32(data, channels, buffer);
                    break;
                case (WaveFormatEncoding.Pcm, 16):
                    AppendPcm16(data, channels, buffer);
                    break;
                case (WaveFormatEncoding.Pcm, 32):
                    AppendPcm32(data, channels, buffer);
                    break;
                default:
                    _logger.LogError("Unsupported capture format {Encoding}/{Bits}.", format.Encoding, format.BitsPerSample);
                    break;
            }
        }
    }

    private static void AppendFloat32(ReadOnlySpan<byte> data, int channels, List<float> sink)
    {
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
        for (var i = 0; i + channels <= floats.Length; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += floats[i + c];
            sink.Add(sum / channels);
        }
    }

    private static void AppendPcm16(ReadOnlySpan<byte> data, int channels, List<float> sink)
    {
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data);
        for (var i = 0; i + channels <= shorts.Length; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += shorts[i + c] / 32768f;
            sink.Add(sum / channels);
        }
    }

    private static void AppendPcm32(ReadOnlySpan<byte> data, int channels, List<float> sink)
    {
        var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(data);
        for (var i = 0; i + channels <= ints.Length; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += ints[i + c] / 2147483648f;
            sink.Add(sum / channels);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "Capture stopped with an error.");

        _stopped?.TrySetResult();
    }

    // -------------------------------------------------------------- helpers

    private MMDevice ResolveDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            try
            {
                return enumerator.GetDevice(_settings.DeviceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture device '{Id}' unavailable; falling back to the default.",
                    _settings.DeviceId);
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;

        var source = new FloatArraySampleProvider(input, fromRate);
        var resampler = new WdlResamplingSampleProvider(source, toRate);

        var expected = (int)((long)input.Length * toRate / fromRate) + 64;
        var output = new float[expected];

        var total = 0;
        while (total < output.Length)
        {
            var read = resampler.Read(output, total, output.Length - total);
            if (read == 0) break;
            total += read;
        }

        return total == output.Length ? output : output[..total];
    }

    /// <summary>Adapts an in-memory mono float array to NAudio's ISampleProvider.</summary>
    private sealed class FloatArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _capture?.StopRecording();
            _capture?.Dispose();
        }
        catch
        {
            // Nothing useful to do while tearing down.
        }

        _gate.Dispose();
    }
}
