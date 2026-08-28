using NAudio.Wave;

namespace MeetingRecorder.Services;

/// <summary>
/// The setup screen's input check — no file is written.
///
/// It reports more than a level, because "no signal" has two very different
/// causes and the operator cannot act on them the same way: a device that
/// never delivers a single buffer is the wrong device or one Windows is
/// withholding, while a device delivering buffers of silence is muted or
/// pointed away. <see cref="BuffersReceived"/> is what tells those apart.
/// </summary>
public sealed class InputLevelMeter : IDisposable
{
    private IWaveIn? _waveIn;
    private WaveFormat? _format;

    public event Action<double>? LevelChanged;

    /// <summary>Buffers delivered since the last <see cref="Start"/>. Zero means nothing is arriving.</summary>
    public long BuffersReceived { get; private set; }

    /// <summary>When the current device was opened, for the "is it late or is it dead" question.</summary>
    public DateTime StartedAt { get; private set; } = DateTime.MinValue;

    public bool IsRunning => _waveIn is not null;

    /// <summary>The format the device actually accepted, which is not always the one asked for.</summary>
    public string FormatDescription { get; private set; } = "";

    public void Start(int deviceNumber)
    {
        Stop();

        _waveIn = AudioDevices.OpenAndStart(deviceNumber, out _, out var format);
        _format = format;
        FormatDescription = format.SampleRate + " Hz · " +
                            (format.Channels == 1 ? "mono" : format.Channels == 2 ? "stereo" : format.Channels + " ch");
        BuffersReceived = 0;
        StartedAt = DateTime.UtcNow;
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public void Stop()
    {
        if (_waveIn is null) return;

        _waveIn.DataAvailable -= OnDataAvailable;
        try { _waveIn.StopRecording(); } catch (Exception) { }
        try { _waveIn.Dispose(); } catch (Exception) { }
        _waveIn = null;
        _format = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _format;
        if (format is null) return;

        BuffersReceived++;
        LevelChanged?.Invoke(AudioCaptureService.ComputePeak(e.Buffer, e.BytesRecorded, format));
    }

    public void Dispose() => Stop();
}
