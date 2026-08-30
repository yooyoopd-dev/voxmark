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
///     recording. It never stops — and if no input can be opened at that
///     instant, a watchdog keeps trying until one can, rather than leaving
///     the meeting silently dead.
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

    /// <summary>
    /// Audio actually written, in seconds, accumulated buffer by buffer from
    /// the bytes each one carried. It is the same "count the samples that were
    /// written" rule as before — but accumulated rather than divided at the
    /// end, because a replacement device can deliver a different sample rate
    /// and one bytes-per-second constant can no longer describe the whole
    /// file. Still never the wall clock.
    /// </summary>
    private double _secondsWritten;

    private int _deviceNumber;
    private bool _stopRequested;

    private string _folder = "";
    private string _baseName = "";
    private double _splitSeconds;
    private double _partStartSeconds;

    /// <summary>
    /// When the last capture buffer arrived. Wall clock, deliberately: it
    /// answers "is the device still delivering?", never "how far into the
    /// recording are we?" — that stays on the sample count.
    /// </summary>
    private DateTime _lastBufferAt = DateTime.UtcNow;

    /// <summary>Throttles the re-open attempt below; wall clock, same reasoning.</summary>
    private DateTime _lastWriterRetry = DateTime.MinValue;

    private System.Threading.Timer? _watchdog;
    private int _recovering;
    private bool _announcedOutage;

    /// <summary>How long a silent device is given before it is treated as gone.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);

    public AudioCaptureService(int bitrateKbps = 128) => _bitrateKbps = bitrateKbps;

    public bool IsCapturing { get; private set; }
    public bool IsPaused { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Buffers the encoder refused. Surfaced in the header, never swallowed.</summary>
    public int DroppedBuffers { get; private set; }

    /// <summary>Verbatim <c>audio_format</c> for the Markdown front matter.</summary>
    public string FormatDescription { get; private set; } = "mp3 / 128 kbps / 44100 Hz / mono";

    /// <summary>
    /// The format the device is actually delivering, which is not always the
    /// one that was asked for. Anything that consumes
    /// <see cref="PcmAvailable"/> needs it to make sense of the bytes.
    /// </summary>
    public WaveFormat CurrentFormat => _writerFormat ??
        new WaveFormat(SampleRate, BitsPerSample, Channels);

    /// <summary>Elapsed seconds of audio actually written to the MP3 so far.</summary>
    public double ElapsedSeconds => _secondsWritten;

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

    /// <summary>
    /// The raw capture buffer, in the device's own format, for anything that
    /// wants to listen in — today that is speech recognition.
    ///
    /// A tap, not a stage: it is raised after the audio is safely in the
    /// encoder, and a subscriber that throws is swallowed here, because
    /// nothing downstream of capture is allowed to stop the recording
    /// (section 11). Silent while paused, like the waveform, since paused
    /// time is not in the file.
    /// </summary>
    public event Action<byte[], int, WaveFormat>? PcmAvailable;

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
        _secondsWritten = 0;
        _partStartSeconds = 0;
        _stopRequested = false;
        _announcedOutage = false;
        _deviceNumber = deviceNumber;
        _folder = folder;
        _baseName = baseName;
        _parts.Clear();

        // Opened before the encoder exists, because which format the driver
        // accepts decides what the encoder has to be built for.
        _waveIn = AudioDevices.OpenAndStart(deviceNumber, out var name, out var format);
        DeviceName = name;
        _writerFormat = format;
        FormatDescription = Describe(format, _bitrateKbps);

        _splitSeconds = splitMinutes > 0 ? splitMinutes * 60.0 : 0;

        OpenNextPart();

        _lastBufferAt = DateTime.UtcNow;
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        IsCapturing = true;
        IsPaused = false;

        // The device is watched from here on. RecordingStopped is the polite
        // way a driver says it has gone, and it is not always sent: a device
        // that simply stops delivering buffers used to leave the meeting
        // recording nothing, with the timer still running and no way back.
        _watchdog = new System.Threading.Timer(_ => CheckStillAlive(), null, 1000, 1000);
    }

    /// <summary>
    /// Is audio still arriving? Runs on a timer thread once a second and does
    /// nothing at all in the healthy case.
    /// </summary>
    private void CheckStillAlive()
    {
        if (!IsCapturing || _stopRequested) return;

        // A paused meeting still receives buffers — the pause is downstream of
        // capture — so this check is meaningful while paused too, and a device
        // lost during a pause is found again before the operator resumes.
        if (_waveIn is not null && DateTime.UtcNow - _lastBufferAt < StallTimeout) return;

        Recover(_waveIn is null ? "no input device" : "\"" + DeviceName + "\" stopped delivering audio");
    }

    /// <summary>Close the file being written and start the next one.</summary>
    private void OpenNextPart()
    {
        var index = _parts.Count + 1;

        // Index 1 of an unsplit session keeps the plain name it has always
        // had. A later part can still appear there — a replacement input with
        // a different sample rate cannot be appended to an MP3 already open —
        // and it is numbered rather than overwriting anything.
        var fileName = _splitSeconds > 0 || index > 1
            ? _baseName + "_part" + index.ToString("00") + ".mp3"
            : _baseName + ".mp3";

        _mp3Writer = new LameMP3FileWriter(Path.Combine(_folder, fileName), _writerFormat!, _bitrateKbps);
        _partStartSeconds = ElapsedSeconds;
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
            rolled = RollLocked();
        }
        PartRolled?.Invoke(rolled);
    }

    /// <summary>Close the open file and open the next one. Caller holds the writer lock.</summary>
    private AudioPart RollLocked()
    {
        _parts[^1].EndSeconds = ElapsedSeconds;
        var rolled = _parts[^1];
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
        return rolled;
    }

    /// <summary>
    /// The replacement device delivers a different format. An MP3 is encoded
    /// for one format from its first frame, so the open file is finished and
    /// the recording continues into the next one — which is what keeps the
    /// promise that matters (the meeting is still being recorded) instead of
    /// the one that does not (it is all in a single file).
    ///
    /// The timeline is untouched: <see cref="ElapsedSeconds"/> accumulates
    /// across parts, so a mark made after the swap means what it says.
    /// </summary>
    private void SwitchFormat(WaveFormat format)
    {
        AudioPart rolled;
        lock (_writerLock)
        {
            var previous = Describe(_writerFormat!, _bitrateKbps);
            _writerFormat = format;
            var current = Describe(format, _bitrateKbps);

            // audio_format has to describe every file the session produced,
            // so a change is appended rather than replacing what came before.
            if (!FormatDescription.EndsWith(current, StringComparison.Ordinal))
            {
                FormatDescription = (FormatDescription.Length > 0 ? FormatDescription : previous)
                    + " → " + current + " (from part " + (_parts.Count + 1) + ")";
            }

            rolled = RollLocked();
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

        _watchdog?.Dispose();
        _watchdog = null;

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
        // Before anything else: a buffer that arrives after Stop — from a
        // device a recovery adopted as the meeting was ending — belongs to
        // nothing and must not re-open a file.
        if (_stopRequested || !IsCapturing) return;

        var format = _writerFormat;
        if (format is null) return;

        _lastBufferAt = DateTime.UtcNow;

        LevelChanged?.Invoke(ComputePeak(e.Buffer, e.BytesRecorded, format));

        if (IsPaused) return;

        // Cut the buffer before writing, so each slice carries the file time
        // it actually starts at rather than the time the buffer ended.
        var slices = ComputeSlices(e.Buffer, e.BytesRecorded, format, ElapsedSeconds);

        try
        {
            lock (_writerLock)
            {
                // No encoder means the last attempt to open a file failed —
                // a full disk, a folder that went away with a USB drive. Try
                // again, once a second, rather than discarding the rest of
                // the meeting in silence. A constructor that throws adds no
                // part, so a failed retry costs nothing but the attempt.
                if (_mp3Writer is null && DateTime.UtcNow - _lastWriterRetry > TimeSpan.FromSeconds(1))
                {
                    _lastWriterRetry = DateTime.UtcNow;
                    try { OpenNextPart(); } catch (Exception) { }
                }

                if (_mp3Writer is null)
                {
                    // The clock only counts audio that reached a file, so it
                    // does not advance here — the same rule that makes a
                    // paused span simply absent.
                    DroppedBuffers++;
                }
                else
                {
                    _mp3Writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _bytesWritten += e.BytesRecorded;
                    _secondsWritten += e.BytesRecorded / (double)Math.Max(1, format.AverageBytesPerSecond);
                    if (_parts.Count > 0) _parts[^1].EndSeconds = ElapsedSeconds;
                }
            }
        }
        catch (Exception)
        {
            // Never let an encode hiccup take the recording down; count it
            // instead so the header can show it.
            DroppedBuffers++;
        }

        if (slices.Length > 0) SlicesAvailable?.Invoke(slices);

        if (PcmAvailable is { } tap)
        {
            try
            {
                tap(e.Buffer, e.BytesRecorded, format);
            }
            catch (Exception)
            {
                // A listener's problem is never the recording's problem.
            }
        }

        if (_splitSeconds > 0 && ElapsedSeconds - _partStartSeconds >= _splitSeconds) RollPart();
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
        Recover("\"" + DeviceName + "\" disappeared");
    }

    /// <summary>
    /// Put the recording back on an input, whatever it takes.
    ///
    /// Reached from two directions — the driver said it stopped, or the
    /// watchdog noticed the buffers had — and it accepts a device whose
    /// format differs from the open MP3's by rolling to a new file for it.
    /// Refusing such a device is what used to end the meeting: connect a
    /// Bluetooth headset and Windows re-shuffles the inputs, so the
    /// replacement is very often 16 or 48 kHz where the file was 44.1.
    ///
    /// If nothing can be opened this second, it says so once and returns; the
    /// watchdog calls again a second later, and the meeting resumes by itself
    /// the moment an input exists.
    /// </summary>
    private void Recover(string reason)
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;

        try
        {
            // Stop() can land between the watchdog deciding to act and this
            // line; re-opening a device after that would leave one running
            // with nothing to write into.
            if (_stopRequested || !IsCapturing) return;

            DetachDevice();

            foreach (var candidate in FallbackCandidates())
            {
                IWaveIn device;
                string name;
                WaveFormat format;
                try
                {
                    device = AudioDevices.OpenAndStart(candidate, out name, out format);
                }
                catch (Exception)
                {
                    continue;
                }

                bool reformatted;
                try
                {
                    reformatted = !format.Equals(_writerFormat);
                    if (reformatted) SwitchFormat(format);

                    _deviceNumber = candidate;
                    DeviceName = name;
                    _lastBufferAt = DateTime.UtcNow;
                    _waveIn = device;
                    device.DataAvailable += OnDataAvailable;
                    device.RecordingStopped += OnRecordingStopped;

                    // Stop() may have landed while this candidate was opening.
                    if (_stopRequested || !IsCapturing)
                    {
                        DetachDevice();
                        return;
                    }
                }
                catch (Exception)
                {
                    // Opening worked but adopting it did not; leave the
                    // device closed and let the watchdog come round again.
                    try { device.StopRecording(); } catch (Exception) { }
                    try { device.Dispose(); } catch (Exception) { }
                    continue;
                }

                _announcedOutage = false;
                DeviceChanged?.Invoke("Input " + reason + " — recording continued on \"" + name + "\"" +
                                      (reformatted
                                          ? ", in " + _parts[^1].FileName + ": it delivers " +
                                            format.SampleRate + " Hz where the previous file was encoded for " +
                                            "another rate, which no single MP3 can hold."
                                          : "."));
                return;
            }

            if (_announcedOutage) return;
            _announcedOutage = true;
            DeviceChanged?.Invoke("Input " + reason + " and nothing else would open — everything recorded so far " +
                                  "is safe on disk, and recording resumes by itself as soon as an input is available.");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private void DetachDevice()
    {
        var old = _waveIn;
        _waveIn = null;
        if (old is null) return;

        old.DataAvailable -= OnDataAvailable;
        old.RecordingStopped -= OnRecordingStopped;
        try { old.StopRecording(); } catch (Exception) { }
        try { old.Dispose(); } catch (Exception) { }
    }

    /// <summary>
    /// Inputs to try, best first. The device that was recording leads —
    /// looked up by the <em>name</em> it had, because device numbers shift
    /// when one appears or disappears, and a headset connecting mid-meeting
    /// is exactly that: the number we opened is now somebody else.
    /// </summary>
    private IEnumerable<int> FallbackCandidates()
    {
        if (_deviceNumber == LoopbackDeviceNumber)
        {
            // A session recording system audio wants system audio back, not
            // the nearest microphone.
            yield return LoopbackDeviceNumber;
        }
        else if (_deviceNumber == AudioDevices.DefaultDeviceNumber)
        {
            yield return AudioDevices.DefaultDeviceNumber;
        }
        else
        {
            var count = DeviceCount();
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(AudioDevices.NameOf(i), DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return i;
                    break;
                }
            }

            yield return _deviceNumber;
            yield return AudioDevices.DefaultDeviceNumber;
        }

        var live = DeviceCount();
        for (var i = 0; i < live; i++) yield return i;

        if (_deviceNumber != LoopbackDeviceNumber) yield return LoopbackDeviceNumber;
    }

    private static int DeviceCount()
    {
        try
        {
            return WaveInEvent.DeviceCount;
        }
        catch (Exception)
        {
            return 0;
        }
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

    public void Dispose()
    {
        Stop();
        _watchdog?.Dispose();
        _watchdog = null;
    }
}
