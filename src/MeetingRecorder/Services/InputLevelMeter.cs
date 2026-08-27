using NAudio.Wave;

namespace MeetingRecorder.Services;

/// <summary>Cheap standalone level meter for the setup screen's input check — no file is written.</summary>
public sealed class InputLevelMeter : IDisposable
{
    private IWaveIn? _waveIn;

    public event Action<double>? LevelChanged;

    public void Start(int deviceNumber)
    {
        Stop();

        if (deviceNumber == AudioCaptureService.LoopbackDeviceNumber)
        {
            _waveIn = new WasapiLoopbackCapture();
        }
        else
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(AudioCaptureService.SampleRate, AudioCaptureService.BitsPerSample, AudioCaptureService.Channels),
                BufferMilliseconds = 50,
            };
        }
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        if (_waveIn is null) return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveIn != null)
        {
            LevelChanged?.Invoke(AudioCaptureService.ComputePeak(e.Buffer, e.BytesRecorded, _waveIn.WaveFormat));
        }
    }

    public void Dispose() => Stop();
}
