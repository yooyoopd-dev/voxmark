using NAudio.Lame;
using NAudio.Wave;

namespace MeetingRecorder.Services;

/// <summary>
/// Captures the selected input device continuously and encodes to MP3 in
/// real time via a bundled LAME encoder (no ffmpeg process, no runtime
/// download). Elapsed "file time" is derived from PCM samples actually
/// written, not the wall clock: <see cref="Pause"/> keeps the device
/// running but stops feeding the encoder, so paused spans are simply
/// absent from the file rather than needing to be cut out afterwards.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    private const int Mp3BitrateKbps = 128;

    private static readonly WaveFormat Format = new(SampleRate, BitsPerSample, Channels);
    public const int LoopbackDeviceNumber = -1;

    private double _bytesPerSecond = SampleRate * Channels * (BitsPerSample / 8.0);

    private IWaveIn? _waveIn;
    private LameMP3FileWriter? _mp3Writer;
    private long _bytesWritten;

    public bool IsCapturing { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>Elapsed seconds of audio actually written to the MP3 so far.</summary>
    public double ElapsedSeconds => _bytesWritten / _bytesPerSecond;

    /// <summary>Fires on every buffer with a 0..1 peak level, for the live meter.</summary>
    public event Action<double>? LevelChanged;

    public static IReadOnlyList<(int Id, string Name)> GetInputDevices()
    {
        var devices = new List<(int Id, string Name)>
        {
            (LoopbackDeviceNumber, "System Audio (Loopback)")
        };
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            devices.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        }
        return devices;
    }

    public void Start(int deviceNumber, string mp3FilePath)
    {
        if (IsCapturing) throw new InvalidOperationException("Already capturing.");

        _bytesWritten = 0;
        
        if (deviceNumber == LoopbackDeviceNumber)
        {
            _waveIn = new WasapiLoopbackCapture();
        }
        else
        {
            _waveIn = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = Format, BufferMilliseconds = 50 };
        }
        
        _bytesPerSecond = _waveIn.WaveFormat.AverageBytesPerSecond;
        _mp3Writer = new LameMP3FileWriter(mp3FilePath, _waveIn.WaveFormat, Mp3BitrateKbps);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();

        IsCapturing = true;
        IsPaused = false;
    }

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    /// <summary>Stops capture and finalises the MP3 file.</summary>
    public void Stop()
    {
        if (!IsCapturing) return;

        _waveIn!.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;

        _mp3Writer!.Dispose();
        _mp3Writer = null;

        IsCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveIn != null)
        {
            LevelChanged?.Invoke(ComputePeak(e.Buffer, e.BytesRecorded, _waveIn.WaveFormat));
        }

        if (IsPaused) return;

        _mp3Writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _bytesWritten += e.BytesRecorded;
    }

    /// <summary>PCM peak level in the buffer, normalised to 0..1.</summary>
    public static double ComputePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            float max = 0;
            var floatBuffer = new WaveBuffer(buffer).FloatBuffer;
            int samples = bytesRecorded / 4;
            for (int i = 0; i < samples; i++)
            {
                var abs = Math.Abs(floatBuffer[i]);
                if (abs > max) max = abs;
            }
            return max;
        }

        // int, not short: Math.Abs(short.MinValue) is 32768, which overflows
        // a short and would wrap around to a negative "peak" if truncated.
        var peak = 0;
        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var abs = Math.Abs((int)sample);
            if (abs > peak) peak = abs;
        }
        return peak / 32768.0;
    }

    public void Dispose()
    {
        if (IsCapturing) Stop();
    }
}
