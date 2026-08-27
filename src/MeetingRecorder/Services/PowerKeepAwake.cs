using System.Runtime.InteropServices;

namespace MeetingRecorder.Services;

/// <summary>
/// Section 11: "Sleep and display-off are inhibited for the session
/// duration. The screen dims but does not lock." Held for as long as the
/// meeting is recording, released on Stop.
/// </summary>
public static class PowerKeepAwake
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public static void Begin() =>
        SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);

    public static void End() => SetThreadExecutionState(EsContinuous);
}
