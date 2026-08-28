namespace MeetingRecorder.Models;

/// <summary>
/// One short window of captured audio reduced to what a waveform needs:
/// the extremes and the loudness.
///
/// A single peak per capture buffer is enough for a level meter but far too
/// coarse to place a mark against — 50 ms of speech collapses to one bar and
/// every syllable disappears. Slices are cut at ~10 ms instead, and keeping
/// <see cref="Min"/> and <see cref="Max"/> separately is what makes the drawn
/// envelope asymmetric like a real waveform rather than a mirrored blob.
/// </summary>
public readonly record struct WaveSlice(double Seconds, float Min, float Max, float Rms);
