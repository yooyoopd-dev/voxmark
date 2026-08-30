using MeetingRecorder.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace MeetingRecorder.Services;

/// <summary>
/// Live speech recognition, running beside the recorder rather than in front
/// of it. Full edition only.
///
/// The rule this whole class is shaped by is design guide section 11:
/// <b>recording never stops for any reason</b>. So the capture thread's only
/// job here is to copy samples into a queue and leave; every expensive or
/// fallible thing — resampling, whisper, the GPU — happens on a worker thread
/// that cannot reach the encoder. If that worker dies, throws, or falls
/// behind, the meeting still records and the operator is told in a sentence.
///
/// Timestamps line up with the marks by construction. Audio is consumed
/// strictly in order and nothing is discarded silently, so a chunk's start is
/// exactly <c>framesConsumed / sourceSampleRate</c> — the same "count the
/// samples that were written" rule <see cref="AudioCaptureService"/> uses for
/// file time. That is what makes attributing a segment to a mark meaningful
/// rather than approximate.
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    /// <summary>Whisper's own input rate. Everything is resampled to this.</summary>
    private const int WhisperSampleRate = 16000;

    /// <summary>
    /// Below this there is not enough context for a decent decode; above it,
    /// the operator is watching a blank strip for too long. Six seconds is
    /// the compromise the live view is tuned around.
    /// </summary>
    private const double MinChunkSeconds = 6.0;

    /// <summary>Whisper's window is 30 s; staying well under it keeps latency honest.</summary>
    private const double MaxChunkSeconds = 18.0;

    /// <summary>
    /// How far behind the transcriber may fall before it starts dropping
    /// audio. Two minutes at 44.1 kHz mono float is about 21 MB — cheap
    /// enough to absorb a slow patch, small enough that it cannot grow into
    /// the recording's memory.
    /// </summary>
    private const double MaxBacklogSeconds = 120.0;

    private readonly SessionOptions _options;
    private readonly object _lock = new();
    private readonly Queue<float[]> _pending = new();
    private readonly ManualResetEventSlim _work = new(false);

    private Thread? _worker;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    private int _sourceSampleRate = AudioCaptureService.SampleRate;
    private int _pendingSamples;
    private int _headOffset;
    private long _consumedFrames;

    /// <summary>Where the chunk being decoded starts, so the segment callback can offset into it.</summary>
    private double _chunkStartSeconds;
    private volatile bool _stopping;
    private volatile bool _faulted;

    public TranscriptionService(SessionOptions options) => _options = options;

    /// <summary>A recognised span, on the recording's own timebase. Raised on the worker thread.</summary>
    public event Action<TranscriptSegment>? SegmentRecognised;

    /// <summary>A line for the transcript strip's header. Raised on the worker thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Model name, language and runtime, verbatim for the Markdown.</summary>
    public string Description { get; private set; } = "";

    /// <summary>
    /// Set when recognition started but not on the engine this machine could
    /// have used — in practice an NVIDIA machine that fell back to the CPU
    /// because the CUDA libraries are not installed. Not a failure: the
    /// meeting transcribes either way, it just runs about five times slower,
    /// and the operator is owed the reason rather than being left to wonder
    /// why the live text is a long way behind the room.
    /// </summary>
    public string? RuntimeWarning { get; private set; }

    /// <summary>Seconds of audio dropped because recognition could not keep up. Never hidden.</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>True once the worker is running and accepting audio.</summary>
    public bool IsRunning => _worker is not null && !_faulted;

    /// <summary>Seconds of captured audio not yet recognised.</summary>
    public double BacklogSeconds
    {
        get
        {
            lock (_lock) return _pendingSamples / (double)_sourceSampleRate;
        }
    }

    /// <summary>
    /// Load the model and start the worker. Returns null on success or a
    /// sentence to show the operator; it never throws, because the caller is
    /// on the path that starts a recording.
    /// </summary>
    public string? Start(WaveFormat sourceFormat)
    {
        try
        {
            if (WhisperRuntime.Probe() is { } runtimeProblem) return Fault(runtimeProblem);

            var model = WhisperRuntime.ResolveModel(_options.WhisperModelPath);
            if (!model.IsUsable) return Fault(model.Problem ?? "No speech model is available.");

            _sourceSampleRate = Math.Max(8000, sourceFormat.SampleRate);

            // Building the factory is what actually loads the native library,
            // so the runtime label is only meaningful after this line.
            _factory = WhisperFactory.FromPath(model.Path, new WhisperFactoryOptions { UseGpu = true });

            var builder = _factory.CreateBuilder()
                // Leave one core for the recorder and the UI; whisper will
                // happily saturate everything otherwise.
                .WithThreads(Math.Max(1, Environment.ProcessorCount - 2))
                // The pool hands back rented strings that must be returned,
                // and these strings outlive the callback — they go into the
                // journal and the UI.
                .WithoutStringPool()
                .WithSegmentEventHandler(OnSegment);

            builder = string.Equals(_options.TranscriptionLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(string.IsNullOrWhiteSpace(_options.TranscriptionLanguage)
                    ? "en"
                    : _options.TranscriptionLanguage);

            _processor = builder.Build();

            Description = WhisperRuntime.ShortModelName(model.Name) + " / " +
                          (_options.TranscriptionLanguage.Length > 0 ? _options.TranscriptionLanguage : "en") + " / " +
                          WhisperRuntime.LoadedRuntimeLabel;

            // The factory is built, so the loader has run and the answer is
            // real rather than predicted — the one moment this can be known
            // for certain.
            DiagnoseRuntime();

            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "VoxMark transcription",
                // Below normal on purpose: the encoder and the UI must win
                // every contest for the CPU with this thread.
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();

            StatusChanged?.Invoke(model.Warning ?? "listening");
            return null;
        }
        catch (Exception ex)
        {
            return Fault("Speech recognition could not start: " + ex.Message);
        }
    }

    /// <summary>
    /// Record which engine actually loaded, and say so when it is the slow
    /// one on a machine that could have done better. Whisper.net falls back
    /// from CUDA to the CPU silently, and that fallback costs roughly a
    /// five-fold slowdown — the difference between a transcript a few seconds
    /// behind the room and one twenty seconds behind it.
    /// </summary>
    private void DiagnoseRuntime()
    {
        try
        {
            var gpu = WhisperRuntime.InspectGpu();
            AppPaths.Note("Speech recognition loaded the " + WhisperRuntime.LoadedRuntimeLabel +
                          " engine from " + (WhisperRuntime.RuntimeRoot ?? "an unknown location") + ".\n" +
                          "NVIDIA driver: " + (gpu.HasNvidiaDriver ? "yes" : "no") +
                          " · CUDA engine in this build: " + (gpu.HasCudaBackend ? "yes" : "no") +
                          " · CUDA libraries: " + (gpu.Missing.Count == 0
                              ? "found in " + (gpu.LibrariesFoundIn ?? "the search path")
                              : "missing " + string.Join(", ", gpu.Missing)));

            if (!string.Equals(WhisperRuntime.LoadedRuntimeLabel, "CPU", StringComparison.OrdinalIgnoreCase)) return;

            RuntimeWarning = WhisperRuntime.GpuAdvice(gpu);
        }
        catch (Exception)
        {
            // Diagnostics never get to affect whether recognition runs.
        }
    }

    private string Fault(string message)
    {
        _faulted = true;
        StatusChanged?.Invoke(message);
        return message;
    }

    /// <summary>
    /// Hand over one capture buffer. Called on NAudio's capture thread, so
    /// this does the least work that is possible: downmix to mono and copy.
    /// No resampling, no allocation beyond the one array, no lock held across
    /// anything that could block.
    /// </summary>
    public void Push(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (_stopping || _faulted || _worker is null) return;

        try
        {
            var mono = ToMono(buffer, bytesRecorded, format);
            if (mono.Length == 0) return;

            lock (_lock)
            {
                // Falling behind is a real possibility on a CPU-only machine
                // with a large model. Dropping the oldest audio keeps the
                // live view current — which is what it is for — and the
                // dropped total is reported rather than swallowed.
                var limit = (int)(MaxBacklogSeconds * _sourceSampleRate);
                while (_pendingSamples + mono.Length > limit && _pending.Count > 0)
                {
                    var head = _pending.Dequeue();
                    var dropped = head.Length - _headOffset;
                    _pendingSamples -= dropped;
                    _consumedFrames += dropped;
                    _headOffset = 0;
                    DroppedSeconds += dropped / (double)_sourceSampleRate;
                }

                _pending.Enqueue(mono);
                _pendingSamples += mono.Length;
            }

            _work.Set();
        }
        catch (Exception)
        {
            // Whatever went wrong here, it does not get to reach the encoder.
        }
    }

    /// <summary>
    /// Average the channels into mono float. Handles the two formats a
    /// Windows input device actually produces — 16-bit PCM and 32-bit float.
    /// </summary>
    private static float[] ToMono(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var bytesPerSample = isFloat ? 4 : Math.Max(1, format.BitsPerSample / 8);
        if (!isFloat && bytesPerSample != 2) return Array.Empty<float>();

        var frames = bytesRecorded / (bytesPerSample * channels);
        if (frames <= 0) return Array.Empty<float>();

        var mono = new float[frames];
        var floats = isFloat ? new WaveBuffer(buffer).FloatBuffer : null;

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                if (isFloat)
                {
                    sum += floats![index];
                }
                else
                {
                    var offset = index * 2;
                    sum += (short)(buffer[offset] | (buffer[offset + 1] << 8)) / 32768.0;
                }
            }
            mono[frame] = (float)(sum / channels);
        }

        return mono;
    }

    // ---------------------------------------------------------------- worker

    private void Run()
    {
        try
        {
            while (true)
            {
                var chunk = ChooseChunk(out var chunkStartSeconds);
                if (chunk is null)
                {
                    // Nothing left and nothing coming: the flush is done.
                    if (_stopping) break;
                    _work.Reset();
                    _work.Wait(250);
                    continue;
                }

                Transcribe(chunk, chunkStartSeconds);
            }
        }
        catch (Exception ex)
        {
            Fault("Speech recognition stopped: " + ex.Message + " — the recording is unaffected.");
        }
    }

    /// <summary>
    /// Pull the next chunk out of the pending queue, or null when there is
    /// not enough audio yet. While stopping, whatever is left counts as
    /// enough — a final half-sentence is better than losing it.
    /// </summary>
    private float[]? ChooseChunk(out double chunkStartSeconds)
    {
        // The copy and the cut search both run under the lock — a couple of
        // milliseconds every several seconds, against NAudio's 100 ms
        // buffers. Releasing between them would let a backlog drop shift the
        // queue underneath the offsets, which is a correctness problem, and
        // this is not a latency one.
        lock (_lock)
        {
            chunkStartSeconds = _consumedFrames / (double)_sourceSampleRate;

            var minimum = (int)(MinChunkSeconds * _sourceSampleRate);
            if (_pendingSamples == 0 || (!_stopping && _pendingSamples < minimum)) return null;

            var window = Math.Min(_pendingSamples, (int)(MaxChunkSeconds * _sourceSampleRate));
            var chunk = new float[window];
            CopyPending(chunk, window);

            // Cut in a pause rather than mid-word when there is room to
            // choose; a chunk boundary through a word costs both halves.
            var take = _stopping && _pendingSamples <= window
                ? window
                : QuietestCut(chunk, minimum);

            if (take < window) Array.Resize(ref chunk, take);
            DiscardPending(take);
            return chunk;
        }
    }

    /// <summary>Copy the first <paramref name="count"/> pending samples without consuming them.</summary>
    private void CopyPending(float[] destination, int count)
    {
        var written = 0;
        var offset = _headOffset;

        foreach (var block in _pending)
        {
            if (written >= count) break;
            var available = block.Length - offset;
            var take = Math.Min(available, count - written);
            Array.Copy(block, offset, destination, written, take);
            written += take;
            offset = 0;
        }
    }

    private void DiscardPending(int count)
    {
        var remaining = count;
        while (remaining > 0 && _pending.Count > 0)
        {
            var head = _pending.Peek();
            var available = head.Length - _headOffset;
            if (available > remaining)
            {
                _headOffset += remaining;
                remaining = 0;
            }
            else
            {
                _pending.Dequeue();
                _headOffset = 0;
                remaining -= available;
            }
        }

        _pendingSamples -= count - remaining;
        _consumedFrames += count - remaining;
    }

    /// <summary>
    /// The end of the quietest 200 ms window in the last 40 % of the chunk —
    /// the most likely pause, and therefore the least damaging place to cut.
    /// Falls back to the whole chunk when nothing is quieter than anything
    /// else, which is what continuous speech looks like.
    /// </summary>
    private int QuietestCut(float[] chunk, int minimum)
    {
        var probe = Math.Max(1, (int)(0.2 * _sourceSampleRate));
        var from = Math.Max(minimum, (int)(chunk.Length * 0.6));
        if (chunk.Length - from <= probe) return chunk.Length;

        var bestEnergy = double.MaxValue;
        var bestEnd = chunk.Length;

        // Step by a quarter of the probe rather than by sample: 4x resolution
        // on the cut point is far more than the decoder can tell apart, and
        // this runs on every chunk.
        for (var start = from; start + probe <= chunk.Length; start += Math.Max(1, probe / 4))
        {
            double energy = 0;
            for (var i = start; i < start + probe; i++) energy += chunk[i] * (double)chunk[i];

            if (energy >= bestEnergy) continue;
            bestEnergy = energy;
            bestEnd = start + probe;
        }

        return bestEnd;
    }

    private void Transcribe(float[] chunk, double chunkStartSeconds)
    {
        if (_processor is null) return;

        _chunkStartSeconds = chunkStartSeconds;
        try
        {
            _processor.Process(Resample(chunk));
        }
        catch (Exception ex)
        {
            // One bad chunk is not a reason to give up on the rest of the
            // meeting; say so once and carry on with the next.
            StatusChanged?.Invoke("a chunk could not be recognised (" + ex.Message + ") — still listening");
        }
    }

    /// <summary>
    /// Resample one chunk to whisper's 16 kHz. Per-chunk rather than as one
    /// continuous stream: it costs a few milliseconds of filter warm-up at
    /// each boundary, which no decoder can perceive, and buys timestamps that
    /// are exactly the recorder's own — which is the property the mark
    /// mapping depends on.
    /// </summary>
    private float[] Resample(float[] chunk)
    {
        if (_sourceSampleRate == WhisperSampleRate) return chunk;

        var source = new ArraySampleProvider(chunk, _sourceSampleRate);
        var resampler = new WdlResamplingSampleProvider(source, WhisperSampleRate);

        var estimate = (int)(chunk.Length * (WhisperSampleRate / (double)_sourceSampleRate)) + 256;
        var output = new float[estimate];
        var total = 0;

        while (total < output.Length)
        {
            var read = resampler.Read(output, total, output.Length - total);
            if (read == 0) break;
            total += read;
        }

        if (total != output.Length) Array.Resize(ref output, total);
        return output;
    }

    private void OnSegment(SegmentData data)
    {
        var text = data.Text is { } raw ? raw.Trim() : "";
        if (text.Length == 0) return;

        SegmentRecognised?.Invoke(new TranscriptSegment
        {
            StartSeconds = _chunkStartSeconds + data.Start.TotalSeconds,
            EndSeconds = _chunkStartSeconds + data.End.TotalSeconds,
            Text = text,
            Probability = data.Probability,
        });
    }

    /// <summary>
    /// Stop accepting audio and give the worker a bounded window to finish
    /// what is queued. Bounded because this runs on the Stop path, and the
    /// operator pressing Stop must never be left waiting on the GPU.
    /// </summary>
    public void StopAndFlush(TimeSpan timeout)
    {
        if (_worker is null) return;

        _stopping = true;
        _work.Set();

        try
        {
            if (!_worker.Join(timeout))
            {
                StatusChanged?.Invoke("stopped with " + BacklogSeconds.ToString("0") +
                                      " s still unrecognised — the audio is complete in the MP3");
            }
        }
        catch (Exception)
        {
            // Nothing useful to do; the session is already being finalised.
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _work.Set();

        // Only free the native context once the worker is genuinely out of
        // it. Disposing a WhisperProcessor while a decode is still inside is
        // an access violation, not a catchable exception, and it would take
        // the process — and therefore the finalise pass — down with it. A
        // worker that outlived its flush window keeps its objects instead and
        // dies with the process; a leaked native handle at exit costs
        // nothing.
        if (_worker is not null && _worker.IsAlive) return;

        try { _processor?.Dispose(); } catch (Exception) { }
        try { _factory?.Dispose(); } catch (Exception) { }
        _processor = null;
        _factory = null;

        // _work is deliberately not disposed: it never allocates a kernel
        // handle for the Wait/Set/Reset use here, and disposing it could
        // throw inside a worker that is still waiting on it.
    }

    /// <summary>
    /// The float array as an <see cref="ISampleProvider"/>, which is what the
    /// resampler pulls from. NAudio has no built-in one over a plain array.
    /// </summary>
    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples, int sampleRate)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var take = Math.Min(count, _samples.Length - _position);
            if (take <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, take);
            _position += take;
            return take;
        }
    }
}
