using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace FloatingLyrics.Services;

public sealed class SystemAudioSpectrumAnalyzer : IDisposable
{
    private const int FftLength = 2048;
    private const int FftExponent = 11; // log2(2048)
    private const float SilenceFloor = 1.0e-7f;
    private static readonly TimeSpan RoutingRefreshInterval = TimeSpan.FromSeconds(1.25);
    private static readonly TimeSpan PeakRefreshInterval = TimeSpan.FromMilliseconds(45);

    private static readonly HashSet<string> GenericSourceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "desktop", "windows", "store", "player", "music", "media",
        "service", "client", "web", "browser", "runtime", "shell", "package"
    };

    private readonly object gate = new();
    private readonly object routeGate = new();
    private readonly Complex[] fftFrame = new Complex[FftLength];
    private readonly Complex[] fftWorking = new Complex[FftLength];
    private readonly float[] window = CreateHannWindow(FftLength);
    private readonly float[] latestMagnitudes = new float[FftLength / 2];
    private readonly float[] spectrumSnapshot = new float[FftLength / 2];
    private readonly object spectrumReadGate = new();
    private readonly List<(AudioSessionControl Session, AudioMeterInformation Meter)> cachedTargetSessions = [];

    private WasapiLoopbackCapture? capture;
    private MMDeviceEnumerator? sessionEnumerator;
    private MMDevice? renderDevice;
    private int fftIndex;
    private int sampleRate = 48000;
    private bool hasSpectrum;
    private bool isDisposed;

    private string preferredSourceAppId = string.Empty;
    private string normalizedSourceAppId = string.Empty;
    private string executableHint = string.Empty;
    private string[] sourceTokens = [];
    private HashSet<uint> preferredProcessIds = [];
    private DateTimeOffset lastRoutingRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastPeakRefreshUtc = DateTimeOffset.MinValue;
    private float lastPeakValue;
    private float appScopedGate = 1f;
    private bool appScopedGateInitialized;
    private string captureRouteDescription = "system-loopback";

    public string CaptureRouteDescription
    {
        get
        {
            lock (routeGate)
            {
                return captureRouteDescription;
            }
        }
    }

    public void Start()
    {
        if (isDisposed || capture is not null)
        {
            return;
        }

        var loopback = new WasapiLoopbackCapture();
        try
        {
            sampleRate = loopback.WaveFormat.SampleRate;
            loopback.DataAvailable += OnDataAvailable;
            capture = loopback;
            loopback.StartRecording();

            lock (routeGate)
            {
                RefreshPreferredRoutingLocked(force: true);
            }
        }
        catch
        {
            capture = null;
            loopback.DataAvailable -= OnDataAvailable;
            try
            {
                loopback.StopRecording();
            }
            catch
            {
            }

            loopback.Dispose();
            lock (routeGate)
            {
                DisposeSessionEndpointLocked();
                captureRouteDescription = "system-loopback";
            }

            throw;
        }
    }

    public void Stop()
    {
        var loopback = capture;
        capture = null;
        if (loopback is not null)
        {
            loopback.DataAvailable -= OnDataAvailable;
            try
            {
                loopback.StopRecording();
            }
            catch
            {
            }

            loopback.Dispose();
        }

        lock (gate)
        {
            Array.Fill(latestMagnitudes, 0f);
            hasSpectrum = false;
            fftIndex = 0;
        }

        lock (routeGate)
        {
            appScopedGate = 1f;
            appScopedGateInitialized = false;
            DisposeSessionEndpointLocked();
            captureRouteDescription = "system-loopback";
        }
    }

    /// <summary>Current app-focus gain (1 = plain system loopback), for diagnostics.</summary>
    public float CurrentGate
    {
        get
        {
            lock (routeGate)
            {
                return appScopedGate;
            }
        }
    }

    public void SetPreferredSourceApp(string? sourceAppId)
    {
        var normalized = sourceAppId?.Trim() ?? string.Empty;
        var normalizedForMatch = NormalizeForMatch(normalized);
        var nextExecutableHint = ExtractExecutableHint(normalized);
        var nextTokens = BuildSourceTokens(normalizedForMatch);

        lock (routeGate)
        {
            if (string.Equals(preferredSourceAppId, normalized, StringComparison.Ordinal) &&
                string.Equals(normalizedSourceAppId, normalizedForMatch, StringComparison.Ordinal) &&
                string.Equals(executableHint, nextExecutableHint, StringComparison.Ordinal))
            {
                return;
            }

            preferredSourceAppId = normalized;
            normalizedSourceAppId = normalizedForMatch;
            executableHint = nextExecutableHint;
            sourceTokens = nextTokens;
            preferredProcessIds.Clear();
            DisposeTargetSessionsLocked();
            lastRoutingRefreshUtc = DateTimeOffset.MinValue;
            lastPeakRefreshUtc = DateTimeOffset.MinValue;
            appScopedGate = 1f;
            appScopedGateInitialized = false;
            if (capture is not null)
            {
                RefreshPreferredRoutingLocked(force: true);
            }
        }
    }

    public float[] GetSpectrum(int pointCount, double lowFrequencyFocus = 0)
    {
        if (pointCount <= 0)
        {
            return [];
        }

        var result = new float[pointCount];
        lock (spectrumReadGate)
        {
            int snapshotSampleRate;
            bool hasData;
            lock (gate)
            {
                Array.Copy(latestMagnitudes, spectrumSnapshot, spectrumSnapshot.Length);
                snapshotSampleRate = sampleRate;
                hasData = hasSpectrum;
            }

            if (!hasData)
            {
                return result;
            }

            var nyquist = Math.Max(1, snapshotSampleRate / 2.0);
            var maxFreq = Math.Min(18000, nyquist - 1);
            var minFreq = 24.0;
            if (maxFreq <= minFreq)
            {
                maxFreq = nyquist;
                minFreq = 1;
            }

            var focus = Math.Clamp(lowFrequencyFocus, 0, 1);
            for (var i = 0; i < pointCount; i++)
            {
                var startRatio = i / (double)pointCount;
                var endRatio = (i + 1) / (double)pointCount;
                var startHz = LerpLog(minFreq, maxFreq, WarpForLowFrequencyDetail(startRatio, focus));
                var endHz = LerpLog(minFreq, maxFreq, WarpForLowFrequencyDetail(endRatio, focus));
                var startBin = FrequencyToBin(startHz, nyquist, spectrumSnapshot.Length);
                var endBin = FrequencyToBin(endHz, nyquist, spectrumSnapshot.Length);
                if (endBin < startBin)
                {
                    (startBin, endBin) = (endBin, startBin);
                }

                var maxValue = 0f;
                for (var bin = startBin; bin <= endBin; bin++)
                {
                    maxValue = MathF.Max(maxValue, spectrumSnapshot[bin]);
                }

                result[i] = float.IsFinite(maxValue) ? Math.Clamp(maxValue, 0f, 1f) : 0f;
            }
        }

        var gateGain = GetAppScopedGate();
        if (gateGain < 0.999f)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] *= gateGain;
            }
        }

        return result;
    }

    private float GetAppScopedGate()
    {
        lock (routeGate)
        {
            RefreshPreferredRoutingLocked(force: false);
            if (preferredProcessIds.Count == 0)
            {
                appScopedGate = 1f;
                appScopedGateInitialized = false;
                return 1f;
            }

            // Throttle session metering regardless of whether the last read succeeded: these are
            // cross-process COM calls and used to run every frame when the player was not found.
            var now = DateTimeOffset.UtcNow;
            if (now - lastPeakRefreshUtc < PeakRefreshInterval)
            {
                return appScopedGate;
            }

            var peak = ReadCurrentPeakForTargetProcessesLocked();
            if (!peak.HasValue)
            {
                // The player's audio session could not be metered (e.g. Spotify renders from a
                // child process or another device): fall back to the plain system loopback
                // instead of gating the visualizer down to silence.
                appScopedGate += (1f - appScopedGate) * 0.25f;
                appScopedGateInitialized = true;
                return appScopedGate;
            }

            var normalizedPeak = Math.Clamp(peak.Value, 0f, 1f);
            var target = Math.Clamp(MathF.Pow(normalizedPeak, 0.68f) * 1.06f, 0f, 1f);
            if (target < 0.02f)
            {
                target = 0f;
            }

            if (!appScopedGateInitialized)
            {
                appScopedGate = target;
                appScopedGateInitialized = true;
                return appScopedGate;
            }

            var blend = target > appScopedGate ? 0.46f : 0.18f;
            appScopedGate += (target - appScopedGate) * blend;
            appScopedGate = Math.Clamp(appScopedGate, 0f, 1f);
            return appScopedGate;
        }
    }

    private void RefreshPreferredRoutingLocked(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastRoutingRefreshUtc < RoutingRefreshInterval)
        {
            return;
        }

        lastRoutingRefreshUtc = now;
        preferredProcessIds.Clear();
        DisposeTargetSessionsLocked();

        if (capture is null || string.IsNullOrWhiteSpace(normalizedSourceAppId))
        {
            captureRouteDescription = "system-loopback";
            return;
        }

        EnsureSessionEndpointLocked();
        if (renderDevice is null)
        {
            captureRouteDescription = "system-loopback";
            return;
        }

        try
        {
            renderDevice.AudioSessionManager.RefreshSessions();
            var sessions = renderDevice.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var keepSession = false;
                try
                {
                    var pid = session.GetProcessID;
                    if (pid == 0)
                    {
                        continue;
                    }

                    var processName = TryGetProcessName(pid);
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        continue;
                    }

                    if (IsTargetProcessMatch(processName))
                    {
                        preferredProcessIds.Add(pid);
                        cachedTargetSessions.Add((session, session.AudioMeterInformation));
                        keepSession = true;
                    }
                }
                catch
                {
                }
                finally
                {
                    if (!keepSession)
                    {
                        session.Dispose();
                    }
                }
            }
        }
        catch
        {
            preferredProcessIds.Clear();
            DisposeTargetSessionsLocked();
        }

        if (preferredProcessIds.Count == 0 && !string.IsNullOrWhiteSpace(executableHint))
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(executableHint))
                {
                    try
                    {
                        preferredProcessIds.Add((uint)process.Id);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }
        }

        captureRouteDescription = preferredProcessIds.Count > 0
            ? $"system-loopback + app-focus ({preferredSourceAppId})"
            : $"system-loopback (unresolved {preferredSourceAppId})";
    }

    private float? ReadCurrentPeakForTargetProcessesLocked()
    {
        lastPeakRefreshUtc = DateTimeOffset.UtcNow;
        if (renderDevice is null || cachedTargetSessions.Count == 0)
        {
            return null;
        }

        var maxPeak = 0f;
        var found = false;
        for (var i = cachedTargetSessions.Count - 1; i >= 0; i--)
        {
            var (session, meter) = cachedTargetSessions[i];
            try
            {
                if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                {
                    session.Dispose();
                    cachedTargetSessions.RemoveAt(i);
                    continue;
                }

                maxPeak = Math.Max(maxPeak, meter.MasterPeakValue);
                found = true;
            }
            catch
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                }

                cachedTargetSessions.RemoveAt(i);
            }
        }

        if (!found)
        {
            return null;
        }

        lastPeakValue = maxPeak;
        return maxPeak;
    }

    private bool IsTargetProcessMatch(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(normalizedSourceAppId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(executableHint) &&
            string.Equals(processName, executableHint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedSourceAppId.Contains(processName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var token in sourceTokens)
        {
            if (string.Equals(token, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (token.Length >= 4 && processName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (processName.Length >= 4 && token.Contains(processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TryGetProcessName(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName?.Trim().ToLowerInvariant() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void EnsureSessionEndpointLocked()
    {
        if (renderDevice is not null && sessionEnumerator is not null)
        {
            return;
        }

        DisposeSessionEndpointLocked();

        try
        {
            sessionEnumerator = new MMDeviceEnumerator();
            renderDevice = sessionEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            DisposeSessionEndpointLocked();
        }
    }

    private void DisposeTargetSessionsLocked()
    {
        for (var i = 0; i < cachedTargetSessions.Count; i++)
        {
            try
            {
                cachedTargetSessions[i].Session.Dispose();
            }
            catch
            {
            }
        }

        cachedTargetSessions.Clear();
    }

    private void DisposeSessionEndpointLocked()
    {
        DisposeTargetSessionsLocked();

        try
        {
            renderDevice?.Dispose();
        }
        catch
        {
        }

        try
        {
            sessionEnumerator?.Dispose();
        }
        catch
        {
        }

        renderDevice = null;
        sessionEnumerator = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var loopback = capture;
        if (loopback is null)
        {
            return;
        }

        var format = loopback.WaveFormat;
        if (format.Channels <= 0 || format.BitsPerSample <= 0)
        {
            return;
        }

        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * format.Channels;
        if (frameSize <= 0)
        {
            return;
        }

        var frameCount = e.BytesRecorded / frameSize;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var offset = (frame * frameSize) + (ch * bytesPerSample);
                sum += ReadSample(e.Buffer, offset, format.BitsPerSample, format.Encoding);
            }

            var mono = sum / format.Channels;
            AppendSample(mono);
        }
    }

    private void AppendSample(float sample)
    {
        var index = fftIndex;
        fftFrame[index].X = sample * window[index];
        fftFrame[index].Y = 0;
        index++;
        if (index < FftLength)
        {
            fftIndex = index;
            return;
        }

        fftIndex = 0;
        Array.Copy(fftFrame, fftWorking, FftLength);
        FastFourierTransform.FFT(true, FftExponent, fftWorking);

        lock (gate)
        {
            for (var i = 0; i < latestMagnitudes.Length; i++)
            {
                var real = fftWorking[i].X;
                var imag = fftWorking[i].Y;
                var magnitude = MathF.Sqrt((real * real) + (imag * imag));
                var normalized = NormalizeMagnitude(magnitude);
                latestMagnitudes[i] = (latestMagnitudes[i] * 0.62f) + (normalized * 0.38f);
            }

            hasSpectrum = true;
        }
    }

    private static float ReadSample(byte[] buffer, int offset, int bitsPerSample, WaveFormatEncoding encoding)
    {
        switch (bitsPerSample)
        {
            case 16:
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            case 24:
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0)
                {
                    value |= unchecked((int)0xFF000000);
                }

                return value / 8388608f;
            }
            case 32:
            {
                if (encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                {
                    var asFloat = BitConverter.ToSingle(buffer, offset);
                    if (float.IsFinite(asFloat) && Math.Abs(asFloat) <= 1.5f)
                    {
                        return Math.Clamp(asFloat, -1f, 1f);
                    }
                }

                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
            }
            default:
                return 0f;
        }
    }

    private static float NormalizeMagnitude(float magnitude)
    {
        var db = 20f * MathF.Log10(magnitude + SilenceFloor);
        return Math.Clamp((db + 92f) / 74f, 0f, 1f);
    }

    private static int FrequencyToBin(double frequency, double nyquist, int spectrumLength)
    {
        if (nyquist <= 0 || spectrumLength <= 1)
        {
            return 0;
        }

        var normalized = Math.Clamp(frequency / nyquist, 0, 1);
        return Math.Clamp((int)Math.Round(normalized * (spectrumLength - 1)), 0, spectrumLength - 1);
    }

    private static double LerpLog(double min, double max, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var safeMin = Math.Max(min, 1e-4);
        var safeMax = Math.Max(max, safeMin + 1e-4);
        return safeMin * Math.Pow(safeMax / safeMin, clamped);
    }

    private static double WarpForLowFrequencyDetail(double ratio, double focus)
    {
        var clampedRatio = Math.Clamp(ratio, 0, 1);
        var clampedFocus = Math.Clamp(focus, 0, 1);
        if (clampedFocus <= 0.0001)
        {
            return clampedRatio;
        }

        // More focus => more buckets allocated to low-frequency region.
        var gamma = 1.0 + (clampedFocus * 2.4); // 1.0 .. 3.4
        return Math.Pow(clampedRatio, gamma);
    }

    private static string NormalizeForMatch(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[source.Length];
        var cursor = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            buffer[cursor++] = char.IsLetterOrDigit(ch)
                ? char.ToLowerInvariant(ch)
                : ' ';
        }

        return new string(buffer[..cursor]).Trim();
    }

    private static string ExtractExecutableHint(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return string.Empty;
        }

        var trimmed = sourceAppId.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(trimmed).ToLowerInvariant();
        }

        // Common AUMID fallback: take right-most token after separators.
        var normalized = NormalizeForMatch(trimmed);
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 && !GenericSourceTokens.Contains(token))
            .ToArray();

        return tokens.Length == 0
            ? string.Empty
            : tokens[^1];
    }

    private static string[] BuildSourceTokens(string normalizedSource)
    {
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return [];
        }

        return normalizedSource
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 && !GenericSourceTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static float[] CreateHannWindow(int length)
    {
        var values = new float[length];
        if (length <= 1)
        {
            Array.Fill(values, 1f);
            return values;
        }

        for (var i = 0; i < length; i++)
        {
            values[i] = 0.5f * (1f - MathF.Cos((2f * MathF.PI * i) / (length - 1)));
        }

        return values;
    }

    public void Dispose()
    {
        isDisposed = true;
        Stop();
    }
}
