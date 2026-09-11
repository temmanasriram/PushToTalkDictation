using System.Buffers.Binary;

namespace PushToTalkDictation.Audio;

/// <summary>
/// A finished recording: mono, normalised float PCM at <see cref="SampleRate"/>.
///
/// Engines can consume whichever shape they need — raw float samples (sherpa-onnx,
/// Whisper.net), WAV bytes in memory (HTTP endpoints), or a temp file on disk.
/// </summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples, int sampleRate)
    {
        Samples = samples;
        SampleRate = sampleRate;
    }

    public float[] Samples { get; }
    public int SampleRate { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    public static AudioClip Empty(int sampleRate) => new([], sampleRate);

    /// <summary>Root-mean-square level, used to drop silence before it reaches the model.</summary>
    public float Rms
    {
        get
        {
            if (Samples.Length == 0) return 0f;
            double sum = 0;
            foreach (var s in Samples) sum += (double)s * s;
            return (float)Math.Sqrt(sum / Samples.Length);
        }
    }

    /// <summary>16-bit PCM WAV, headers included. Allocation-light single pass.</summary>
    public byte[] ToWavBytes()
    {
        const int headerSize = 44;
        var dataSize = Samples.Length * 2;
        var buffer = new byte[headerSize + dataSize];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + dataSize);
        "WAVE"u8.CopyTo(span.Slice(8, 4));
        "fmt "u8.CopyTo(span.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);        // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);         // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), 1);         // mono
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), 2);         // block align
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), 16);        // bits per sample
        "data"u8.CopyTo(span.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataSize);

        var audio = span[headerSize..];
        for (var i = 0; i < Samples.Length; i++)
        {
            var v = (short)Math.Clamp(Samples[i] * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(audio.Slice(i * 2, 2), v);
        }

        return buffer;
    }

    /// <summary>Writes a temp WAV for engines that only accept file paths. Caller deletes it.</summary>
    public async Task<string> WriteTempWavAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ptt-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, ToWavBytes(), ct).ConfigureAwait(false);
        return path;
    }
}
