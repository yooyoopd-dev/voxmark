using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace MeetingRecorder.Services;

/// <summary>
/// Captures the selected input device continuously and encodes to MP3 in
/// real time through a bundled LAME encoder — no ffmpeg process and no
/// runtime download, matching the design guide's build note.
///
/// Section 11's non-negotiables that live here:
///   - Capture and encoding run on NAudio's own capture thread; the UI never
///     touches them, and a key press is accepted even mid-encode.
///   - Elapsed "file time" is derived from PCM bytes actually written, not
///     the wall clock, so drift cannot desynchronise the marks from the file.
///   - <see cref="Pause"/> keeps the device open but stops feeding the
///     encoder, so a paused span is simply absent from the MP3 rather than
///     needing to be cut out afterwards.
///   - A device unplugged mid-session falls back to another input and keeps
///     recording. It never stops.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>
    /// Not in the design guide — the guide assumes the room mic and puts
    /// system-audio capture explicitly out of scope — but it was already in
    /// the app, so it stays as an opt-in device entry rather than silently
    /// disappearing. The real format is reported into the Markdown either way.
    /// </summary>
    public const int LoopbackDeviceNumber = -1;

    private static readonly WaveFormat MicFormat = new(SampleRate, BitsPerSample, Channels);

    private readonly int _bitrateKbps;

    private IWaveIn? _waveIn;
    private LameMP3FileWriter? _mp3Writer;
    private WaveFormat? _writerFormat;
    private long _bytesWritten;
    private double _bytesPerSecond = SampleRate * Channels * (BitsPerSample / 8.0);
    private int _deviceNumber;
    private bool _stopRequested;

    public AudioCaptureService(int bitrateKbps = 128) => _bitrateKbps = bitrateKbps;

    public bool IsCapturing { get; private set; }
    public bool IsPaused { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Buffers the encoder refused. Surfaced in the header, never swallowed.</summary>
    public int DroppedBuffers { get; private set; }

    /// <summary>Verbatim <c>audio_format</c> for the Markdown front matter.</summary>
    public string FormatDescription { get; private set; } = "mp3 / 128 kbps / 44100 Hz / mono";

    /// <summary>Elapsed seconds of audio actually written to the MP3 so far.</summary>
    public double ElapsedSeconds => _bytesWritten / _bytesPerSecond;

    public long BytesWritten => _bytesWritten;

    /// <summary>Bytes on disk, for the header's "Written to disk" readout.</summary>
    public double WrittenMegabytes => ElapsedSeconds * _bitrateKbps * 1000.0 / 8.0 / 1024.0 / 1024.0;

    /// <summary>Fires on every capture buffer with a 0..1 peak, for the live waveform.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Raised when the input device changed underneath us, with a line for the banner.</summary>
    public event Action<string>? DeviceChanged;

    public static IReadOnlyList<(int Id, string Name)> GetInputDevices()
    {
        var devices = new List<(int Id, string Name)>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try
            {
                devices.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
            }
            catch (Exception)
            {
                // A device can vanish between the count and the query.
            }
        }
        devices.Add((LoopbackDeviceNumber, "System audio (loopback)"));
        return devices;
    }

    public void Start(int deviceNumber, string mp3FilePath)
    {
        if (IsCapturing) throw new InvalidOperationException("Already capturing.");

        _bytesWritten = 0;
        _stopRequested = false;
        _deviceNumber = deviceNumber;

        _waveIn = CreateDevice(deviceNumber, out var name);
        DeviceName = name;
        _writerFormat = _waveIn.WaveFormat;
        _bytesPerSecond = _writerFormat.AverageBytesPerSecond;
        FormatDescription = Describe(_writerFormat, _bitrateKbps);

        _mp3Writer = new LameMP3FileWriter(mp3FilePath, _writerFormat, _bitrateKbps);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        IsCapturing = true;
        IsPaused = false;
    }

    private static IWaveIn CreateDevice(int deviceNumber, out string name)
    {
        if (deviceNumber == LoopbackDeviceNumber)
        {
            var capture = new WasapiLoopbackCapture();
            name = SafeLoopbackName();
            return capture;
        }

        name = WaveInEvent.GetCapabilities(deviceNumber).ProductName;
        return new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = MicFormat,
            BufferMilliseconds = 50,
        };
    }

    private static string SafeLoopbackName()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return "System audio · " + device.FriendlyName;
        }
        catch (Exception)
        {
            return "System audio (loopback)";
        }
    }

    private static string Describe(WaveFormat format, int bitrateKbps)
    {
        var channels = format.Channels == 1 ? "mono" : format.Channels == 2 ? "stereo" : format.Channels + " ch";
        return "mp3 / " + bitrateKbps + " kbps / " + format.SampleRate + " Hz / " + channels;
    }

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    /// <summary>Stops capture and finalises the MP3 file.</summary>
    public void Stop()
    {
        if (!IsCapturing) return;
        _stopRequested = true;
        IsCapturing = false;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } catch (Exception) { }
            _waveIn.Dispose();
            _waveIn = null;
        }

        _mp3Writer?.Dispose();
        _mp3Writer = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _writerFormat;
        if (format is not null)
        {
            LevelChanged?.Invoke(ComputePeak(e.Buffer, e.BytesRecorded, format));
        }

        if (IsPaused || _stopRequested) return;

        try
        {
            _mp3Writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _bytesWritten += e.BytesRecorded;
        }
        catch (Exception)
        {
            // Never let an encode hiccup take the recording down; count it
            // instead so the header can show it.
            DroppedBuffers++;
        }
    }

    /// <summary>
    /// The device went away. Recording never stops for any reason, so pick
    /// another input with the same format and keep writing into the same file.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopRequested || e.Exception is null) return;

        var failed = DeviceName;
        var old = _waveIn;
        if (old is not null)
        {
            old.DataAvailable -= OnDataAvailable;
            old.RecordingStopped -= OnRecordingStopped;
            try { old.Dispose(); } catch (Exception) { }
        }
        _waveIn = null;

        foreach (var candidate in FallbackCandidates())
        {
            try
            {
                var device = CreateDevice(candidate, out var name);
                if (!device.WaveFormat.Equals(_writerFormat))
                {
                    // A different format cannot be appended to the MP3 that
                    // is already open, so this candidate is no good.
                    device.Dispose();
                    continue;
                }

                _waveIn = device;
                _deviceNumber = candidate;
                DeviceName = name;
                device.DataAvailable += OnDataAvailable;
                device.RecordingStopped += OnRecordingStopped;
                device.StartRecording();
                DeviceChanged?.Invoke("Input \"" + failed + "\" disappeared — recording continued on \"" + name + "\".");
                return;
            }
            catch (Exception)
            {
                // Try the next one.
            }
        }

        DeviceChanged?.Invoke("Input \"" + failed + "\" disappeared and no compatible replacement was found — " +
                              "the file is intact but no new audio is being captured.");
    }

    private IEnumerable<int> FallbackCandidates()
    {
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            if (i != _deviceNumber) yield return i;
        }
        if (_deviceNumber != LoopbackDeviceNumber) yield return LoopbackDeviceNumber;
    }

    /// <summary>PCM peak level in the buffer, normalised to 0..1.</summary>
    public static double ComputePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var floats = new WaveBuffer(buffer).FloatBuffer;
            var samples = bytesRecorded / 4;
            float max = 0;
            for (var i = 0; i < samples; i++)
            {
                var abs = Math.Abs(floats[i]);
                if (abs > max) max = abs;
            }
            return Math.Min(1.0, max);
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

    public void Dispose() => Stop();
}
