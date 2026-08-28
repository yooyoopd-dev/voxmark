using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder.Services;

/// <summary>
/// Opening a capture device, in one place, for both the setup meter and the
/// recorder — so the input the level check proves is exactly the input that
/// gets recorded.
///
/// Two things here exist because of real failures rather than tidiness:
///
///   - <b>The default entry.</b> The list used to start at WaveIn device 0,
///     which is simply the first device Windows enumerates and is often not
///     the microphone the user actually selected in Sound settings. Picking
///     it looks like a working setup that records silence. WAVE_MAPPER
///     (<see cref="DefaultDeviceNumber"/>) follows the system default, so the
///     out-of-the-box choice is the one Windows already considers correct.
///   - <b>Format fallback.</b> Asking every device for 44.1 kHz mono is a
///     guess. Plenty of USB and Bluetooth mics are natively 48 kHz stereo,
///     and a driver that refuses the requested format leaves the app open but
///     silent. Candidates are tried in order until one actually starts.
/// </summary>
public static class AudioDevices
{
    /// <summary>WAVE_MAPPER — whatever Windows currently calls the default input.</summary>
    public const int DefaultDeviceNumber = -1;

    /// <summary>System audio rather than a microphone. Opt-in; see AudioCaptureService.</summary>
    public const int LoopbackDeviceNumber = -2;

    /// <summary>Preferred first, then the formats real hardware most often insists on.</summary>
    public static IEnumerable<WaveFormat> CandidateFormats()
    {
        yield return new WaveFormat(44100, 16, 1);
        yield return new WaveFormat(48000, 16, 1);
        yield return new WaveFormat(44100, 16, 2);
        yield return new WaveFormat(48000, 16, 2);
        yield return new WaveFormat(16000, 16, 1);
    }

    public static IReadOnlyList<(int Id, string Name)> List()
    {
        var devices = new List<(int Id, string Name)> { (DefaultDeviceNumber, DefaultName()) };

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

    public static string NameOf(int deviceNumber)
    {
        if (deviceNumber == DefaultDeviceNumber) return DefaultName();
        if (deviceNumber == LoopbackDeviceNumber) return LoopbackName();

        try
        {
            return WaveInEvent.GetCapabilities(deviceNumber).ProductName;
        }
        catch (Exception)
        {
            return "Input " + deviceNumber;
        }
    }

    /// <summary>
    /// Open a device and start it, falling back through
    /// <see cref="CandidateFormats"/> until one is accepted. Throws only when
    /// every candidate was refused.
    ///
    /// The caller subscribes to DataAvailable after this returns, so the first
    /// few milliseconds are dropped; that is the cost of learning which format
    /// the driver will actually take, and it lands before anything is marked.
    /// </summary>
    public static IWaveIn OpenAndStart(int deviceNumber, out string name, out WaveFormat format)
    {
        if (deviceNumber == LoopbackDeviceNumber)
        {
            var loopback = new WasapiLoopbackCapture();
            name = LoopbackName();
            format = loopback.WaveFormat;
            loopback.StartRecording();
            return loopback;
        }

        name = NameOf(deviceNumber);
        Exception? refused = null;

        foreach (var candidate in CandidateFormats())
        {
            var device = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = candidate,
                BufferMilliseconds = 50,
            };

            try
            {
                device.StartRecording();
                format = candidate;
                return device;
            }
            catch (Exception ex)
            {
                refused = ex;
                try { device.Dispose(); } catch (Exception) { }
            }
        }

        throw refused ?? new InvalidOperationException(
            "\"" + name + "\" would not open in any supported format.");
    }

    private static string DefaultName()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return "Windows default input · " + device.FriendlyName;
        }
        catch (Exception)
        {
            return "Windows default input";
        }
    }

    private static string LoopbackName()
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
}
