using System.IO;
using MeetingRecorder.Models;
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
///
/// It can also roll to a new MP3 every N minutes. The rolled files are still
/// one continuous recording as far as the marks are concerned:
/// <see cref="ElapsedSeconds"/> keeps counting across parts, so a mark's
/// timestamp never depends on which file it landed in.
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
    public const int LoopbackDeviceNumber = AudioDevices.LoopbackDeviceNumber;

    private readonly int _bitrateKbps;

    private readonly List<AudioPart> _parts = new();
    private readonly object _writerLock = new();

    private IWaveIn? _waveIn;
    private LameMP3FileWriter? _mp3Writer;
    private WaveFormat? _writerFormat;
    private long _bytesWritten;
    private double _bytesPerSecond = SampleRate * Channels * (BitsPerSample / 8.0);
    private int _deviceNumber;
    private bool _stopRequested;

    private string _folder = "";
    private string _baseName = "";
    private long _splitBytes;
    private long _partStartBytes;

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

    /// <summary>Fires on every capture buffer with a 0..1 peak, for the level meter.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>
    /// Fires on every capture buffer with that buffer cut into ~10 ms slices,
    /// which is the resolution the waveform needs to be worth looking at.
    /// Silent while paused, because paused time is not in the file.
    /// </summary>
    public event Action<WaveSlice[]>? SlicesAvailable;

    /// <summary>Fires when the recorder rolled over to a new MP3.</summary>
    public event Action<AudioPart>? PartRolled;

    /// <summary>Every MP3 written so far, in order.</summary>
    public IReadOnlyList<AudioPart> Parts => _parts;

    /// <summary>Raised when the input device changed underneath us, with a line for the banner.</summary>
    public event Action<string>? DeviceChanged;

    public static IReadOnlyList<(int Id, string Name)> GetInputDevices() => AudioDevices.List();

    /// <summary>
    /// Begin recording into <paramref name="folder"/>. With
    /// <paramref name="splitMinutes"/> at 0 the whole meeting goes into
    /// <c>{baseName}.mp3</c>; otherwise it rolls through
    /// <c>{baseName}_part01.mp3</c>, <c>_part02</c>, … every that many minutes.
    /// </summary>
    public void Start(int deviceNumber, string folder, string baseName, int splitMinutes)
    {
        if (IsCapturing) throw new InvalidOperationException("Already capturing.");

        _bytesWritten = 0;
        _partStartBytes = 0;
        _stopRequested = false;
        _deviceNumber = deviceNumber;
        _folder = folder;
        _baseName = baseName;
        _parts.Clear();

        // Opened before the encoder exists, because which format the driver
        // accepts decides what the encoder has to be built for.
        _waveIn = AudioDevices.OpenAndStart(deviceNumber, out var name, out var format);
        DeviceName = name;
        _writerFormat = format;
        _bytesPerSecond = format.AverageBytesPerSecond;
        FormatDescription = Describe(format, _bitrateKbps);

        // A split is expressed in bytes so the check on the capture thread is
        // an integer comparison rather than a division per buffer.
        _splitBytes = splitMinutes > 0
            ? (long)(splitMinutes * 60.0 * _bytesPerSecond)
            : 0;

        OpenNextPart();

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        IsCapturing = true;
        IsPaused = false;
    }

    /// <summary>Close the file being written and start the next one.</summary>
    private void OpenNextPart()
    {
        var index = _parts.Count + 1;
        var fileName = _splitBytes > 0
            ? _baseName + "_part" + index.ToString("00") + ".mp3"
            : _baseName + ".mp3";

        _mp3Writer = new LameMP3FileWriter(Path.Combine(_folder, fileName), _writerFormat!, _bitrateKbps);
        _partStartBytes = _bytesWritten;
        _parts.Add(new AudioPart
        {
            Index = index,
            FileName = fileName,
            StartSeconds = ElapsedSeconds,
            EndSeconds = ElapsedSeconds,
        });
    }

    /// <summary>
    /// Finish the current file and open the next. Called from the capture
    /// thread between buffers, so no audio is in flight while the encoder is
    /// swapped.
    /// </summary>
    private void RollPart()
    {
        AudioPart rolled;
        lock (_writerLock)
        {
            _parts[^1].EndSeconds = ElapsedSeconds;
            rolled = _parts[^1];
            try
            {
                _mp3Writer?.Dispose();
            }
            catch (Exception)
            {
                // A failed flush costs this part's tail, not the recording.
                DroppedBuffers++;
            }
            _mp3Writer = null;
            OpenNextPart();
        }
        PartRolled?.Invoke(rolled);
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

        lock (_writerLock)
        {
            if (_parts.Count > 0) _parts[^1].EndSeconds = ElapsedSeconds;
            _mp3Writer?.Dispose();
            _mp3Writer = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _writerFormat;
        if (format is null) return;

        LevelChanged?.Invoke(ComputePeak(e.Buffer, e.BytesRecorded, format));

        if (IsPaused || _stopRequested) return;

        // Cut the buffer before writing, so each slice carries the file time
        // it actually starts at rather than the time the buffer ended.
        var slices = ComputeSlices(e.Buffer, e.BytesRecorded, format, ElapsedSeconds);

        try
        {
            lock (_writerLock)
            {
                _mp3Writer?.Write(e.Buffer, 0, e.BytesRecorded);
                _bytesWritten += e.BytesRecorded;
                if (_parts.Count > 0) _parts[^1].EndSeconds = ElapsedSeconds;
            }
        }
        catch (Exception)
        {
            // Never let an encode hiccup take the recording down; count it
            // instead so the header can show it.
            DroppedBuffers++;
        }

        if (slices.Length > 0) SlicesAvailable?.Invoke(slices);

        if (_splitBytes > 0 && _bytesWritten - _partStartBytes >= _splitBytes) RollPart();
    }

    /// <summary>
    /// Reduce a capture buffer to ~10 ms slices of min / max / RMS. Min and
    /// max are kept apart so the drawn waveform is asymmetric the way real
    /// audio is, and RMS gives the solid core inside the peak envelope.
    /// </summary>
    public static WaveSlice[] ComputeSlices(byte[] buffer, int bytesRecorded, WaveFormat format,
                                            double startSeconds, double sliceSeconds = 0.01)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameBytes = bytesPerSample * channels;
        var frames = bytesRecorded / frameBytes;
        if (frames <= 0) return Array.Empty<WaveSlice>();

        var framesPerSlice = Math.Max(1, (int)(format.SampleRate * sliceSeconds));
        var sliceCount = (frames + framesPerSlice - 1) / framesPerSlice;
        var slices = new WaveSlice[sliceCount];

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var floats = isFloat ? new WaveBuffer(buffer).FloatBuffer : null;

        for (var s = 0; s < sliceCount; s++)
        {
            var from = s * framesPerSlice;
            var to = Math.Min(frames, from + framesPerSlice);

            float min = 0, max = 0;
            double sumSquares = 0;
            var count = 0;

            for (var frame = from; frame < to; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = frame * channels + channel;
                    float value;
                    if (isFloat)
                    {
                        value = floats![index];
                    }
                    else
                    {
                        var offset = index * 2;
                        value = (short)(buffer[offset] | (buffer[offset + 1] << 8)) / 32768f;
                    }

                    if (value < min) min = value;
                    if (value > max) max = value;
                    sumSquares += value * (double)value;
                    count++;
                }
            }

            var rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;
            slices[s] = new WaveSlice(
                startSeconds + from / (double)format.SampleRate,
                Math.Clamp(min, -1f, 0f),
                Math.Clamp(max, 0f, 1f),
                (float)Math.Clamp(rms, 0, 1));
        }

        return slices;
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
                var device = AudioDevices.OpenAndStart(candidate, out var name, out var format);
                if (!format.Equals(_writerFormat))
                {
                    // A different format cannot be appended to the MP3 that
                    // is already open, so this candidate is no good.
                    try { device.StopRecording(); } catch (Exception) { }
                    device.Dispose();
                    continue;
                }

                _waveIn = device;
                _deviceNumber = candidate;
                DeviceName = name;
                device.DataAvailable += OnDataAvailable;
                device.RecordingStopped += OnRecordingStopped;
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
        // Whatever Windows now calls the default is the best first guess when
        // the device that was unplugged was the previous default.
        if (_deviceNumber != AudioDevices.DefaultDeviceNumber) yield return AudioDevices.DefaultDeviceNumber;

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
