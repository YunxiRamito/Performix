using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace PreySense.Core.Hardware;

/// <summary>
/// Read-only client for the Predator Sense service named pipe
/// (<c>\\.\pipe\predatorsense_service_namedpipe</c>, service <c>PSSvc</c>).
/// </summary>
/// <remarks>
/// <para>
/// Protocol (reversed from the OEM service, see <c>reverse/pipe/PAYLOAD-NOTES.md</c>):
/// </para>
/// <code>
/// request  : [i16 cmd][u8 numArgs][i32 argLen][arg payload...]
/// response : [u8 numArgs][i32 argLen][payload...]
/// </code>
/// <para>
/// Sensor reads use cmd 15 (<c>kSvcCmdWMIGetGamingSystemInformation</c>) with one
/// <c>u32</c> argument <c>1 | (sensorId &lt;&lt; 8)</c>. The reply payload is 8 bytes:
/// <c>status = raw &amp; 0xFF</c> (0 = OK) and <c>value = (raw &gt;&gt; 8) &amp; 0xFFFF</c>
/// (°C or RPM, no scaling).
/// </para>
/// <para>
/// SAFETY: the OEM service terminates its own process on a malformed frame, so this class
/// only ever emits the exact 11-byte cmd-15 read frame; there is no generic "send anything"
/// entry point. Only read commands are implemented — no Set command is reachable from here.
/// </para>
/// <para>
/// Failure policy: every public read returns <c>null</c> (never throws to callers) and the
/// failure is negatively cached, so a missing/stopped <c>PSSvc</c> cannot stall the UI or the
/// fan-curve timer with a connect timeout on every tick.
/// </para>
/// </remarks>
public sealed class AcerPipeSensorSource
{
    /// <summary>Named pipe the Predator Sense service listens on.</summary>
    public const string PipeName = "predatorsense_service_namedpipe";

    /// <summary>Fully qualified device path of <see cref="PipeName"/>.</summary>
    public const string PipePath = @"\\.\pipe\" + PipeName;

    /// <summary>OEM service that owns the pipe (StartMode=Manual).</summary>
    public const string ServiceName = "PSSvc";

    /// <summary>cmd = <c>kSvcCmdWMIGetGamingSystemInformation</c> (read-only).</summary>
    public const short CmdGetGamingSystemInformation = 15;

    // Sensor ids (System_Health_Information_Index).
    public const int SensorCpuTemperature = 1;
    public const int SensorCpuFanRpm = 2;
    public const int SensorSystemTemperature = 3;
    public const int SensorGpuFanRpm = 6;
    public const int SensorGpuTemperature = 10;

    /// <summary>Hot-path connect timeout: keep a missing service from stalling telemetry.</summary>
    public const int DefaultConnectTimeoutMs = 300;

    /// <summary>Probe/self-test connect timeout (service may be starting up).</summary>
    public const int ProbeConnectTimeoutMs = 2000;

    private const int ExpectedPayloadLength = 8;
    private const int MinRetryDelayMs = 5000;

    private readonly object _gate = new();
    // E3: transport serialization is separate from the state lock so a slow/hung pipe cannot
    // block IsPipeAvailable/LastError/Probe callers for an unbounded time.
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private const int IoLockTimeoutMs = 1000;
    private readonly int _connectTimeoutMs;
    private bool _lastAvailable;
    private long _retryAfterTimestamp;
    private string? _lastError;

    public AcerPipeSensorSource(int connectTimeoutMs = DefaultConnectTimeoutMs)
    {
        _connectTimeoutMs = Math.Max(50, connectTimeoutMs);
    }

    /// <summary>True if the last complete sensor read succeeded (i.e. the pipe answered).</summary>
    public bool IsPipeAvailable
    {
        get { lock (_gate) return _lastAvailable; }
    }

    /// <summary>Last transport/protocol error, for the diagnostics probe.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>The connected pipe name, for logging/UI.</summary>
    public static string DiagnosticName => PipePath;

    // ---- Sensor convenience readers -------------------------------------------------

    public int? ReadCpuTemperature() => ReadSensorValue(SensorCpuTemperature);

    public int? ReadGpuTemperature() => ReadSensorValue(SensorGpuTemperature);

    public int? ReadCpuFanRpm() => ReadSensorValue(SensorCpuFanRpm);

    public int? ReadGpuFanRpm() => ReadSensorValue(SensorGpuFanRpm);

    /// <summary>Reads one sensor id. Returns null when the pipe is unavailable or the id has no data.</summary>
    public int? ReadSensorValue(int sensorId) =>
        TryReadSensor(sensorId, out int value, out _, out _) ? value : null;

    /// <summary>
    /// Reads one sensor id. Never throws. <paramref name="raw"/> is the decoded 64-bit reply
    /// (only meaningful when the method returns true; on an unsupported id the reply is
    /// <c>status=0,value=0</c>, which is exposed as <c>false</c>).
    /// </summary>
    public bool TryReadSensor(int sensorId, out int value, out ulong raw, out string? error)
    {
        value = 0;
        raw = 0;
        error = null;

        if (sensorId is < 0 or > 255)
        {
            error = $"sensor id {sensorId} is out of range";
            return false;
        }

        if (!_ioGate.Wait(IoLockTimeoutMs))
        {
            error = "pipe busy (another command is in flight)";
            return false;
        }

        try
        {
            // Negative cache: do not retry a dead service on every telemetry tick.
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (!_lastAvailable && now < Interlocked.Read(ref _retryAfterTimestamp))
                {
                    error = _lastError ?? "pipe unavailable (negative cache)";
                    return false;
                }
            }

            try
            {
                using var pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.None);

                pipe.Connect(_connectTimeoutMs);

                byte[] request = BuildSensorRequest(sensorId);
                pipe.Write(request, 0, request.Length);
                pipe.Flush();

                byte[] header = ReadExact(pipe, 5);
                int numArgs = header[0];
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
                if (numArgs != 1 || payloadLength != ExpectedPayloadLength)
                {
                    error = $"unexpected reply header (numArgs={numArgs}, len={payloadLength}); expected 1/8";
                    MarkUnavailable(error);
                    return false;
                }

                byte[] payload = ReadExact(pipe, payloadLength);
                raw = BinaryPrimitives.ReadUInt64LittleEndian(payload);

                int status = (int)(raw & 0xFF);
                if (status != 0)
                {
                    MarkAvailable();
                    error = $"sensor {sensorId} status={status}";
                    return false;
                }

                value = (int)((raw >> 8) & 0xFFFF);
                MarkAvailable();
                if (value <= 0)
                {
                    error = $"sensor {sensorId} returned no data (value=0)";
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException
                                          or TimeoutException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                MarkUnavailable(error);
                return false;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private void MarkAvailable()
    {
        _lastAvailable = true;
        _lastError = null;
        Interlocked.Exchange(ref _retryAfterTimestamp, 0);
    }

    private void MarkUnavailable(string? error)
    {
        _lastAvailable = false;
        _lastError = error;
        long retryAt = Stopwatch.GetTimestamp()
                       + (long)(Stopwatch.Frequency * (MinRetryDelayMs / 1000.0));
        Interlocked.Exchange(ref _retryAfterTimestamp, retryAt);
    }

    /// <summary>
    /// Builds the exact 11-byte cmd-15 request for one sensor id.
    /// Layout: <c>[0F 00][01][04 00 00 00][u32 (1 | id&lt;&lt;8)]</c>.
    /// </summary>
    internal static byte[] BuildSensorRequest(int sensorId)
    {
        if (sensorId is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(sensorId), sensorId, "sensor id must fit in one byte");

        byte[] frame = new byte[11];
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(0, 2), CmdGetGamingSystemInformation);
        frame[2] = 1; // numArgs
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3, 4), 4); // arg length
        uint argument = 1u | ((uint)sensorId << 8);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7, 4), argument);
        return frame;
    }

    /// <summary>Decodes a cmd-15 reply payload (8 bytes). Used by the probe and unit tests.</summary>
    internal static bool TryDecodeSensorReply(ReadOnlySpan<byte> payload, out int status, out int value)
    {
        status = -1;
        value = -1;
        if (payload.Length < ExpectedPayloadLength) return false;

        ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(payload[..ExpectedPayloadLength]);
        status = (int)(raw & 0xFF);
        value = (int)((raw >> 8) & 0xFFFF);
        return true;
    }

    // ---- Operating-mode channel (cmd 32/33) ------------------------------------------
    // The direct root\WMI SetGamingMiscSetting call fails on this chassis (ACPI returns a
    // non-zero status), but the OEM service's cmd 32 does the real work. cmd 33 reads it back
    // and is the only accepted confirmation: the cmd 32 reply (0xFFFFFFFF) is NOT a status flag.

    /// <summary>cmd = kSvcCmdWMISetFunction (SetGamingMiscSetting) — one <c>ulong</c> argument.</summary>
    public const short CmdSetGamingMiscSetting = 32;

    /// <summary>cmd = kSvcCmdWMIGetFunction (GetGamingMiscSetting) — one <c>uint32</c> argument.</summary>
    public const short CmdGetGamingMiscSetting = 33;

    /// <summary>Selector of the Acer operating-mode misc setting (low byte of the payload).</summary>
    public const uint OperatingModeSelector = 0x0B;

    /// <summary>
    /// Reads the live Acer operating mode through the pipe (cmd 33).
    /// Reply: <c>status = raw &amp; 0xFF</c> (0 = OK), <c>mode = (raw >> 8) &amp; 0xFF</c>.
    /// </summary>
    public bool TryGetPowerMode(out byte mode, out string status)
    {
        mode = 0;
        if (!TrySendCommand(CmdGetGamingMiscSetting, BuildUIntArgument(OperatingModeSelector), out ulong raw, out string? error))
        {
            status = $"pipe GetGamingMiscSetting failed: {error}";
            return false;
        }

        int serviceStatus = (int)(raw & 0xFF);
        if (serviceStatus != 0)
        {
            status = $"GetGamingMiscSetting returned status={serviceStatus}";
            return false;
        }

        mode = (byte)((raw >> 8) & 0xFF);
        status = $"mode 0x{mode:X2}";
        return true;
    }

    /// <summary>
    /// Writes the Acer operating mode through the pipe (cmd 32) and confirms it with cmd 33.
    /// No optimistic result: the caller only gets true when the readback matches.
    /// </summary>
    public bool TrySetPowerMode(byte mode, out string status)
    {
        ulong payload = OperatingModeSelector | ((ulong)mode << 8);
        byte[] argument = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(argument, payload);

        if (!TrySendCommand(CmdSetGamingMiscSetting, argument, out _, out string? error))
        {
            status = $"pipe SetGamingMiscSetting failed: {error}";
            return false;
        }

        // The OEM Set reply (0xFFFFFFFF) is not a status flag — confirm by reading back.
        if (!TryGetPowerMode(out byte actual, out string getStatus))
        {
            status = $"set sent but readback failed: {getStatus}";
            return false;
        }

        if (actual != mode)
        {
            status = $"set 0x{mode:X2} but readback reports 0x{actual:X2}";
            return false;
        }

        status = $"mode set to 0x{mode:X2} (readback confirmed)";
        return true;
    }

    /// <summary>
    /// Sends one strict OEM frame (<paramref name="cmd"/> + exactly one argument) and returns the
    /// reply value. Accepts 4- and 8-byte reply payloads (Set returns u32, Get returns u64).
    /// Never throws; a malformed frame is impossible because only these builders are used.
    /// </summary>
    public bool TrySendCommand(short cmd, byte[] argument, out ulong raw, out string? error)
    {
        raw = 0;
        error = null;

        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (!_lastAvailable && now < Interlocked.Read(ref _retryAfterTimestamp))
            {
                error = _lastError ?? "pipe unavailable (negative cache)";
                return false;
            }

            try
            {
                using var pipe = new NamedPipeClientStream(
                    serverName: ".", pipeName: PipeName,
                    direction: PipeDirection.InOut, options: PipeOptions.None);
                pipe.Connect(_connectTimeoutMs);

                byte[] frame = BuildCommandFrame(cmd, argument);
                pipe.Write(frame, 0, frame.Length);
                pipe.Flush();

                byte[] header = ReadExact(pipe, 5);
                int numArgs = header[0];
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
                if (numArgs != 1 || payloadLength is not (4 or 8))
                {
                    error = $"unexpected reply header (numArgs={numArgs}, len={payloadLength}); expected 1/4 or 1/8";
                    MarkUnavailable(error);
                    return false;
                }

                byte[] payload = ReadExact(pipe, payloadLength);
                raw = payloadLength == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(payload)
                    : BinaryPrimitives.ReadUInt32LittleEndian(payload);
                MarkAvailable();
                return true;
            }
            catch (Exception ex) when (ex is IOException
                                          or TimeoutException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                MarkUnavailable(error);
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the exact request frame for one command with a single argument:
    /// <c>[i16 cmd][u8 1][i32 len][arg bytes]</c>. Exposed for unit tests.
    /// </summary>
    internal static byte[] BuildCommandFrame(short cmd, ReadOnlySpan<byte> argument)
    {
        byte[] frame = new byte[3 + 4 + argument.Length];
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(0, 2), cmd);
        frame[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3, 4), argument.Length);
        argument.CopyTo(frame.AsSpan(7));
        return frame;
    }

    internal static byte[] BuildUIntArgument(uint value)
    {
        byte[] arg = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(arg, value);
        return arg;
    }

    /// <summary>
    /// E3: bounded exact read. The synchronous pipe <see cref="Stream.Read(byte[], int, int)"/> has no
    /// timeout, so the read runs on a worker and the stream is disposed when the budget expires —
    /// that unblocks the reader and the caller treats it as one failed request (negative cache).
    /// </summary>
    private static byte[] ReadExact(Stream stream, int count)
    {
        const int ReadTimeoutMs = 800;
        byte[] buffer = new byte[count];
        var readTask = Task.Run(() =>
        {
            int read = 0;
            while (read < count)
            {
                int chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0)
                    throw new IOException($"pipe closed after {read}/{count} bytes");
                read += chunk;
            }
            return buffer;
        });

        if (!readTask.Wait(ReadTimeoutMs))
        {
            try { stream.Dispose(); } catch { }
            throw new TimeoutException($"pipe read timed out after {ReadTimeoutMs} ms ({count} bytes expected)");
        }
        return readTask.Result;
    }

    // ---- Diagnostics -----------------------------------------------------------------

    /// <summary>One sensor row of a <see cref="Probe"/> run.</summary>
    public readonly record struct SensorProbeRow(
        int SensorId,
        string Name,
        bool Success,
        int? Value,
        ulong Raw,
        int ElapsedMs,
        string? Error);

    /// <summary>Result of a full diagnostics probe (all four sensors, fresh connections).</summary>
    public sealed record ProbeReport(
        bool PipeReachable,
        int TotalElapsedMs,
        string? LastError,
        SensorProbeRow[] Sensors);

    /// <summary>
    /// Reads all four live sensors with a dedicated (longer) connect timeout, bypassing the
    /// negative cache so a self-test always really talks to the service.
    /// </summary>
    public ProbeReport Probe(int connectTimeoutMs = ProbeConnectTimeoutMs)
    {
        var rows = new SensorProbeRow[4];
        int totalSw = Environment.TickCount;

        lock (_gate)
        {
            _lastAvailable = true;
            _lastError = null;
            Interlocked.Exchange(ref _retryAfterTimestamp, 0);
        }

        (int Id, string Name)[] sensors =
        {
            (SensorCpuTemperature, "CPU temperature (id 1)"),
            (SensorGpuTemperature, "GPU temperature (id 10)"),
            (SensorCpuFanRpm, "CPU fan RPM (id 2)"),
            (SensorGpuFanRpm, "GPU fan RPM (id 6)")
        };

        bool reachable = false;
        string? lastError = null;

        for (int i = 0; i < sensors.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok = TryReadSensorWithTimeout(sensors[i].Id, connectTimeoutMs,
                out int value, out ulong raw, out string? error);
            sw.Stop();
            if (ok) reachable = true;
            if (error != null) lastError = error;
            rows[i] = new SensorProbeRow(sensors[i].Id, sensors[i].Name, ok,
                ok ? value : null, raw, (int)sw.ElapsedMilliseconds, error);
        }

        lock (_gate)
        {
            _lastAvailable = reachable;
            if (!reachable)
            {
                _lastError = lastError ?? "pipe unreachable";
                Interlocked.Exchange(ref _retryAfterTimestamp,
                    Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (MinRetryDelayMs / 1000.0)));
            }
        }

        return new ProbeReport(reachable, Environment.TickCount - totalSw, lastError, rows);
    }

    private bool TryReadSensorWithTimeout(
        int sensorId, int connectTimeoutMs, out int value, out ulong raw, out string? error)
    {
        value = 0;
        raw = 0;
        error = null;

        lock (_gate)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    serverName: ".", pipeName: PipeName,
                    direction: PipeDirection.InOut, options: PipeOptions.None);
                pipe.Connect(Math.Max(50, connectTimeoutMs));

                byte[] request = BuildSensorRequest(sensorId);
                pipe.Write(request, 0, request.Length);
                pipe.Flush();

                byte[] header = ReadExact(pipe, 5);
                int numArgs = header[0];
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
                if (numArgs != 1 || payloadLength != ExpectedPayloadLength)
                {
                    error = $"unexpected reply header (numArgs={numArgs}, len={payloadLength})";
                    return false;
                }

                byte[] payload = ReadExact(pipe, payloadLength);
                raw = BinaryPrimitives.ReadUInt64LittleEndian(payload);
                if (!TryDecodeSensorReply(payload, out int status, out value))
                {
                    error = "reply payload could not be decoded";
                    return false;
                }
                if (status != 0)
                {
                    error = $"status={status}";
                    return false;
                }
                if (value <= 0)
                {
                    error = "value=0 (no data for this id on this chassis)";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }
}

