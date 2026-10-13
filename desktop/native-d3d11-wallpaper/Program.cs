using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

ApplicationConfiguration.Initialize();

var bootstrapLogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Mineradio",
    "native-d3d11-wallpaper-debug.log");
Application.ThreadException += (_, e) => BootstrapLog(bootstrapLogPath, $"thread-exception {e.Exception.GetType().Name}: {e.Exception}");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var message = e.ExceptionObject is Exception ex
        ? $"{ex.GetType().Name}: {ex}"
        : e.ExceptionObject?.ToString() ?? "unknown";
    BootstrapLog(bootstrapLogPath, $"unhandled-exception terminating={e.IsTerminating} {message}");
};

var statePath = args.Length > 0 ? args[0] : Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Mineradio",
    "wallpaper-state.json");
var desktopMode = ParseDesktopMode(args);
var debugVisible = args.Any(arg => string.Equals(arg, "--debug-visible", StringComparison.OrdinalIgnoreCase));
var debugBackground = args
    .Select(arg => arg.StartsWith("--debug-bg=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--debug-bg=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "dark";
var capturePath = args
    .Select(arg => arg.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--capture=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
var captureFramesValue = args
    .Select(arg => arg.StartsWith("--capture-frames=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--capture-frames=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
var captureFrames = int.TryParse(captureFramesValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCaptureFrames)
    ? Math.Clamp(parsedCaptureFrames, 1, 600)
    : 48;
var sonicDebugPass = args
    .Select(arg => arg.StartsWith("--sonic-debug-pass=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--sonic-debug-pass=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
var debugTimeOffset = args
    .Select(arg => arg.StartsWith("--debug-time-offset=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--debug-time-offset=".Length) : "")
    .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0f, 120f) : 0f)
    .FirstOrDefault(value => value > 0f);
var debugFixedTimeValue = args
    .Select(arg => arg.StartsWith("--debug-fixed-time=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--debug-fixed-time=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
var debugFixedTime = float.TryParse(debugFixedTimeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDebugFixedTime)
    ? Math.Clamp(parsedDebugFixedTime, 0f, 120f)
    : -1f;
var pipeName = args
    .Select(arg => arg.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase) ? arg.Substring("--pipe=".Length) : "")
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "MineradioD3D11Wallpaper";

using var singleInstanceMutex = new Mutex(true, $@"Global\MineradioD3D11Wallpaper_{pipeName}", out var singleInstanceCreated);
if (!singleInstanceCreated)
{
    BootstrapLog(bootstrapLogPath, $"single-instance-exit pipe={pipeName}");
    return;
}

try
{
    System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
}
catch {}

Application.Run(new D3DWallpaperForm(statePath, desktopMode, pipeName, debugVisible, debugBackground, capturePath, captureFrames, sonicDebugPass, debugTimeOffset, debugFixedTime));

static DesktopMode ParseDesktopMode(string[] args)
{
    if (args.Any(arg => string.Equals(arg, "--debug-visible", StringComparison.OrdinalIgnoreCase))) return DesktopMode.Disabled;
    if (args.Any(arg => arg.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))) return DesktopMode.Disabled;
    if (args.Any(arg => string.Equals(arg, "--desktop-overlay", StringComparison.OrdinalIgnoreCase))) return DesktopMode.OverlayFallback;
    if (args.Any(arg => string.Equals(arg, "--disable-desktop-layer", StringComparison.OrdinalIgnoreCase))) return DesktopMode.Disabled;
    return DesktopMode.DesktopLayer;
}

static void BootstrapLog(string path, string message)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
    catch {}
}

internal sealed class D3DWallpaperForm : Form
{
    private readonly string _statePath;
    private readonly DesktopMode _desktopMode;
    private readonly string _pipeName;
    private readonly bool _debugVisible;
    private readonly bool _debugAudioState;
    private readonly string _logPath;
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 1 };
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _attachTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _overlayRegionTimer = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _fullscreenTimer = new() { Interval = 350 };
    private readonly StopwatchLite _clock = new();
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _pipeCancel = new();
    private WallpaperState _state = new();
    private D3DParticleRenderer? _renderer;
    private IntPtr _attachParent = IntPtr.Zero;
    private string _attachKey = "";
    private Rectangle _attachBounds = Rectangle.Empty;
    private volatile bool _pipeStateReceived;
    private long _lastDebugAudioLogTicks;
    private readonly DesktopAttachManager _desktopAttachManager;
    private DesktopAttachStatus _desktopAttachStatus = DesktopAttachStatus.NotStarted;
    private string _desktopDisabledReason = "";
    private long _pipeConnectedTicks;
    private bool _pipePresetGuardUsed;

    private readonly string _debugBackground;
    private readonly string _capturePath;
    private readonly int _captureFrameLimit;
    private readonly string _sonicDebugPass;
    private readonly float _debugTimeOffset;
    private readonly float _debugFixedTime;
    private int _captureFrameCount;
    private double _nextSonicFrameAtMs;
    private int _scheduledTargetFps;
    private int _recordedTargetFps;
    private WallpaperState? _lastEnabledState;
    private float _disabledFadeStartedAt = -1f;
    private bool _fullscreenPaused;

    public D3DWallpaperForm(string statePath, DesktopMode desktopMode, string pipeName, bool debugVisible, string debugBackground, string capturePath, int captureFrameLimit, string sonicDebugPass, float debugTimeOffset, float debugFixedTime)
    {
        _statePath = statePath;
        _desktopMode = desktopMode;
        _pipeName = pipeName;
        _debugVisible = debugVisible;
        _debugBackground = debugBackground;
        _capturePath = capturePath;
        _captureFrameLimit = captureFrameLimit;
        _sonicDebugPass = string.IsNullOrWhiteSpace(sonicDebugPass) ? "" : sonicDebugPass.Trim().ToLowerInvariant();
        _debugTimeOffset = debugTimeOffset;
        _debugFixedTime = debugFixedTime;
        _debugAudioState = string.Equals(Environment.GetEnvironmentVariable("MINERADIO_DEBUG_NATIVE_AUDIO"), "1", StringComparison.OrdinalIgnoreCase);
        _logPath = Path.Combine(Path.GetDirectoryName(statePath) ?? AppContext.BaseDirectory, "native-d3d11-wallpaper-debug.log");
        _desktopAttachManager = new DesktopAttachManager(Log);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Native.DesktopBounds();
        BackColor = System.Drawing.Color.Black;
        Text = "Mineradio D3D11 Wallpaper";
        RecordFrameRate(_state.TargetFps);
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _frameTimer.Tick += (_, _) => RenderFrame();
        _stateTimer.Tick += (_, _) => LoadState();
        _attachTimer.Tick += (_, _) => ReattachIfNeeded();
        _overlayRegionTimer.Tick += (_, _) => RefreshDesktopOverlayRegion();
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenPause();
        Shown += (_, _) =>
        {
            try
            {
                Log($"[DesktopMode] Desktop mode: {DesktopAttachManager.ModeName(_desktopMode)}");
                if (_desktopMode == DesktopMode.DesktopLayer)
                {
                    ReattachIfNeeded(true);
                }
                else if (_desktopMode == DesktopMode.OverlayFallback)
                {
                    Log("[DesktopMode] overlay-fallback enabled: using TOPMOST transparent overlay");
                    Log("[DesktopOverlay] TOPMOST overlay mode enabled by explicit argument");
                    Native.ShowOverlayFallback(Handle, Bounds);
                    _desktopAttachStatus = DesktopAttachStatus.OverlayFallbackActive;
                }
                else
                {
                    _desktopAttachStatus = DesktopAttachStatus.DisabledWithReason;
                    _desktopDisabledReason = "disabled by --disable-desktop-layer";
                    if (_debugVisible) Show(); else Hide();
                    Log($"[DesktopAttach] Mode changed to disabled-with-reason reason={_desktopDisabledReason}");
                }
                _renderer = new D3DParticleRenderer(Handle, ClientSize.Width, ClientSize.Height, Log, _debugVisible, _debugBackground, _sonicDebugPass);
                LoadState();
                _clock.Start();
                Native.BeginHighResolutionTimer();
                _stateTimer.Start();
                _frameTimer.Start();
                _fullscreenTimer.Start();
                if (_desktopMode == DesktopMode.DesktopLayer || _desktopMode == DesktopMode.OverlayFallback) _attachTimer.Start();
                if (_desktopMode == DesktopMode.OverlayFallback) _overlayRegionTimer.Start();
                UpdateFullscreenPause();
                _ = Task.Run(() => RunStatePipeAsync(_pipeCancel.Token));
                Log($"started hwnd=0x{Handle.ToInt64():X} desktopMode={DesktopAttachManager.ModeName(_desktopMode)} attachStatus={_desktopAttachStatus} debugVisible={_debugVisible} debugBg={_debugBackground} debugTimeOffset={_debugTimeOffset.ToString("0.###", CultureInfo.InvariantCulture)} debugFixedTime={_debugFixedTime.ToString("0.###", CultureInfo.InvariantCulture)} bounds={Bounds.Width}x{Bounds.Height}");
            }
            catch (Exception ex)
            {
                Log($"start-error {ex.GetType().Name}: {ex.Message}");
                Close();
            }
        };
        Resize += (_, _) => _renderer?.Resize(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                if (!_debugVisible)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                    if (_desktopMode == DesktopMode.OverlayFallback) cp.ExStyle |= WS_EX_LAYERED;
                }
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int HTTRANSPARENT = -1;
        const int MA_NOACTIVATEANDEAT = 4;
        if (!_debugVisible && m.Msg == WM_NCHITTEST)
        {
            m.Result = new IntPtr(HTTRANSPARENT);
            return;
        }
        if (!_debugVisible && m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = new IntPtr(MA_NOACTIVATEANDEAT);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) {}
    protected override void OnPaint(PaintEventArgs e) {}

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch {}
    }

    private void RecordFrameRate(int targetFps)
    {
        if (_recordedTargetFps == targetFps) return;
        _recordedTargetFps = targetFps;
        var title = $"Mineradio D3D11 Wallpaper [{targetFps} FPS]";
        if (IsHandleCreated && InvokeRequired) BeginInvoke(new Action(() => Text = title));
        else Text = title;
    }

    private void LoadState()
    {
        try
        {
            if (_pipeStateReceived) return;
            if (!File.Exists(_statePath)) return;
            using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            int targetFps;
            lock (_stateLock)
            {
                _state = WallpaperState.FromJson(doc.RootElement, _state);
                targetFps = _state.TargetFps;
            }
            RecordFrameRate(targetFps);
        }
        catch
        {
            // Electron may replace the file while it is being read. Keep the last good state.
        }
    }

    private void RenderFrame()
    {
        if (_renderer == null) return;
        WallpaperState state;
        lock (_stateLock)
        {
            if (string.IsNullOrWhiteSpace(_capturePath))
            {
                var nowMs = _clock.ElapsedSeconds * 1000.0;
                var targetFps = _state.TargetFps;
                var frameIntervalMs = 1000.0 / targetFps;
                if (_scheduledTargetFps != targetFps || _nextSonicFrameAtMs <= 0.0 || nowMs - _nextSonicFrameAtMs > 250.0)
                {
                    if (_scheduledTargetFps != targetFps)
                    {
                        Log($"frame-rate target={targetFps}");
                        RecordFrameRate(targetFps);
                    }
                    _scheduledTargetFps = targetFps;
                    _nextSonicFrameAtMs = nowMs;
                }
                if (nowMs + 0.20 < _nextSonicFrameAtMs) return;
                do
                {
                    _nextSonicFrameAtMs += frameIntervalMs;
                }
                while (_nextSonicFrameAtMs <= nowMs - frameIntervalMs);
            }
            state = _state.Clone();
        }
        if (!state.Enabled)
        {
            if (_lastEnabledState != null && _lastEnabledState.Preset == 0 && string.IsNullOrWhiteSpace(_capturePath))
            {
                var fadeTime = _debugFixedTime >= 0f ? _debugFixedTime : _clock.ElapsedSeconds + _debugTimeOffset;
                var fadeState = _lastEnabledState.Clone();
                fadeState.Enabled = true;
                fadeState.Playing = false;
                _renderer.Render(fadeState, fadeTime);
                if (_disabledFadeStartedAt < 0f) _disabledFadeStartedAt = fadeTime;
                if (fadeTime - _disabledFadeStartedAt >= 5.05f) Close();
                return;
            }
            Close();
            return;
        }
        _lastEnabledState = state.Clone();
        _disabledFadeStartedAt = -1f;
        var renderTime = _debugFixedTime >= 0f ? _debugFixedTime : _clock.ElapsedSeconds + _debugTimeOffset;
        _renderer.Render(state, renderTime);
        if (!string.IsNullOrWhiteSpace(_capturePath) && ++_captureFrameCount >= _captureFrameLimit)
        {
            try
            {
                _renderer.SaveBackBufferPng(_capturePath);
                Log($"capture-saved path={_capturePath}");
            }
            catch (Exception ex)
            {
                Log($"capture-error {ex.GetType().Name}: {ex.Message}");
            }
            Close();
        }
    }

    private void UpdateFullscreenPause()
    {
        if (_debugVisible
            || _desktopMode == DesktopMode.Disabled
            || !string.IsNullOrWhiteSpace(_capturePath))
        {
            return;
        }

        bool stateEnabled;
        lock (_stateLock)
        {
            stateEnabled = _state.Enabled;
        }

        if (!stateEnabled)
        {
            if (_fullscreenPaused)
            {
                _fullscreenPaused = false;
                _nextSonicFrameAtMs = 0.0;
                _frameTimer.Start();
            }
            return;
        }

        var shouldPause = FullscreenAppDetector.TryGetForegroundFullscreen(
            Handle,
            out var fullscreenApp);
        if (shouldPause == _fullscreenPaused) return;

        _fullscreenPaused = shouldPause;
        if (shouldPause)
        {
            _frameTimer.Stop();
            Log(
                $"fullscreen-pause entered pid={fullscreenApp.ProcessId} "
                + $"class={fullscreenApp.WindowClass} "
                + $"window={fullscreenApp.WindowBounds.X},{fullscreenApp.WindowBounds.Y},"
                + $"{fullscreenApp.WindowBounds.Width}x{fullscreenApp.WindowBounds.Height} "
                + $"monitor={fullscreenApp.MonitorBounds.X},{fullscreenApp.MonitorBounds.Y},"
                + $"{fullscreenApp.MonitorBounds.Width}x{fullscreenApp.MonitorBounds.Height}");
            return;
        }

        if (_desktopMode == DesktopMode.DesktopLayer) ReattachIfNeeded(true);
        _nextSonicFrameAtMs = 0.0;
        _frameTimer.Start();
        Log("fullscreen-pause exited");
    }

    private async Task RunStatePipeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // While no pipe client is connected, keep the file fallback live.
                // Otherwise a dropped pipe leaves the renderer on the last motion
                // packet until the next forced state change.
                _pipeStateReceived = false;
                // Node's net.Socket opens Windows named pipes as a duplex stream.
                // An input-only server pipe makes the client readable side end
                // immediately, which causes Electron to reconnect on every send.
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                Log($"pipe-wait name={_pipeName}");
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                Log($"pipe-connected name={_pipeName}");
                _pipeConnectedTicks = DateTimeOffset.UtcNow.UtcTicks;
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true);
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    ApplyStateJson(line);
                }
                _pipeStateReceived = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _pipeStateReceived = false;
                Log($"pipe-error {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(450, token).ConfigureAwait(false); } catch {}
            }
        }
    }

    private void ApplyStateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            WallpaperState next;
            lock (_stateLock)
            {
                _state = WallpaperState.FromJson(doc.RootElement, _state);
                _state.Preset = ApplyInitialPipePresetGuard(_state.Preset);
                next = _state.Clone();
            }
            RecordFrameRate(next.TargetFps);
            _pipeStateReceived = true;
            MaybeLogDebugAudioState(next);
        }
        catch (Exception ex)
        {
            Log($"pipe-json-error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private int ApplyInitialPipePresetGuard(int incomingPreset)
    {
        if (_pipePresetGuardUsed || _pipeConnectedTicks <= 0) return incomingPreset;
        _pipePresetGuardUsed = true;
        var elapsedTicks = DateTimeOffset.UtcNow.UtcTicks - _pipeConnectedTicks;
        if (elapsedTicks < 0 || elapsedTicks > TimeSpan.FromSeconds(2).Ticks) return incomingPreset;
        var canonicalPreset = ReadCanonicalPreset(incomingPreset);
        if (canonicalPreset == incomingPreset) return incomingPreset;
        _pipePresetGuardUsed = true;
        Log($"pipe-initial-preset-guard incoming={incomingPreset} canonical={canonicalPreset}");
        return canonicalPreset;
    }

    private int ReadCanonicalPreset(int fallback)
    {
        try
        {
            if (!File.Exists(_statePath)) return fallback;
            using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var preset = fallback;
            if (root.TryGetProperty("visual", out var visual) && visual.ValueKind == JsonValueKind.Object)
            {
                preset = visual.GetPreset("preset", preset);
            }
            else
            {
                preset = root.GetPreset("preset", preset);
            }
            preset = Math.Clamp(preset, 0, 7);
            return preset == 6 ? 5 : preset;
        }
        catch
        {
            return fallback;
        }
    }

    private void MaybeLogDebugAudioState(WallpaperState state)
    {
        if (!_debugAudioState && state.Preset != 1) return;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (now - _lastDebugAudioLogTicks < TimeSpan.FromMilliseconds(_debugAudioState ? 500 : 1000).Ticks) return;
        _lastDebugAudioLogTicks = now;
        Log(
            "pipe-audio-state "
            + $"preset={state.Preset} "
            + $"bass={state.Bass:0.000} mid={state.Mid:0.000} treble={state.Treble:0.000} "
            + $"beat={state.Beat:0.000} energy={state.Energy:0.000} "
            + $"sub={state.SubBass:0.000} body={state.BassBody:0.000} lowMid={state.LowMid:0.000} midFlow={state.MidFlow:0.000} highMid={state.HighMid:0.000} "
            + $"presence={state.Presence:0.000} brilliance={state.Brilliance:0.000} air={state.Air:0.000} warmth={state.Warmth:0.000} brightness={state.Brightness:0.000} "
            + $"kick={state.KickEnvelope:0.000} conf={state.KickConfidence:0.000} "
            + $"macro=({state.VisualStructure:0.000},{state.VisualMotion:0.000},{state.VisualImpact:0.000},{state.VisualImpactPulse:0.000},{state.VisualImpactStrength:0.000},{state.VisualGloss:0.000}) "
            + $"eq=({state.VisualEqLowBreath:0.00},{state.VisualEqLowWeight:0.00},{state.VisualEqSlowFlow:0.00},{state.VisualEqMidDirection:0.00},{state.VisualEqEdgePeak:0.00},{state.VisualEqSparkle:0.00},{state.VisualEqAirDust:0.00},{state.VisualEqKickSoft:0.00},{state.VisualEqKickRelease:0.00})"
        );
    }

    private bool ReattachIfNeeded(bool force = false)
    {
        if (_desktopMode == DesktopMode.OverlayFallback)
        {
            var overlayBounds = Native.DesktopBounds();
            var boundsChanged = force || Bounds.Width != overlayBounds.Width || Bounds.Height != overlayBounds.Height || Bounds.X != overlayBounds.X || Bounds.Y != overlayBounds.Y;
            if (!boundsChanged) return true;

            Bounds = overlayBounds;
            _renderer?.Resize(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            Native.ShowOverlayFallback(Handle, overlayBounds);
            Log($"[DesktopOverlay] overlay-fallback position bounds={overlayBounds.Width}x{overlayBounds.Height}");
            return true;
        }

        if (_desktopMode == DesktopMode.Disabled || IsDisposed || !IsHandleCreated) return false;

        var currentParent = Native.ParentOf(Handle);
        if (currentParent == IntPtr.Zero && !force)
        {
            Log("[DesktopAttach] Reattach requested: parent lost");
            force = true;
        }

        if (!force
            && _desktopAttachStatus == DesktopAttachStatus.AttachedToDesktopLayer
            && _attachParent != IntPtr.Zero
            && currentParent == _attachParent
            && Native.IsWindowHandle(_attachParent)
            && Visible)
        {
            return true;
        }

        var result = _desktopAttachManager.AttachToDesktopLayer(Handle, force, _attachParent, _attachBounds, _attachKey);
        _desktopAttachStatus = result.Status;
        if (!result.Ok)
        {
            _desktopDisabledReason = result.Reason;
            Hide();
            return false;
        }

        var shouldApplyBounds = result.Changed
            || Bounds.X != result.Bounds.X
            || Bounds.Y != result.Bounds.Y
            || Bounds.Width != result.Bounds.Width
            || Bounds.Height != result.Bounds.Height;

        if (!Visible) Show();
        if (shouldApplyBounds)
        {
            Bounds = result.Bounds;
            _renderer?.Resize(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        }
        _attachParent = result.Parent;
        _attachKey = result.Key;
        _attachBounds = result.Bounds;
        if (result.Changed)
        {
            Log($"[DesktopAttach] attached target={result.Key} bounds={result.Bounds.Width}x{result.Bounds.Height}");
        }
        return true;
    }

    private void RefreshDesktopOverlayRegion()
    {
        if (_desktopMode != DesktopMode.OverlayFallback || IsDisposed || !IsHandleCreated) return;
        Native.RefreshDesktopOverlayRegion(Handle, Bounds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Dispose();
            _stateTimer.Dispose();
            _attachTimer.Dispose();
            _overlayRegionTimer.Dispose();
            _fullscreenTimer.Dispose();
            _pipeCancel.Cancel();
            _pipeCancel.Dispose();
            _renderer?.Dispose();
            Native.EndHighResolutionTimer();
        }
        base.Dispose(disposing);
    }
}

internal sealed partial class D3DParticleRenderer : IDisposable
{
    private const int CoverParticleGrid = 183;
    private const int CoverParticleCount = CoverParticleGrid * CoverParticleGrid;
    private const int GenericParticleGrid = 167;
    private const int GenericParticleCount = GenericParticleGrid * GenericParticleGrid;
    private const int MaxModelParticleCount = 60000;
    private const int LyricSparkCount = 420;
    private const int NativeRippleCount = 12;
    private const int GalaxyBeatWaveCount = 4;
    private const int SonicTerrainGridLow = 96;
    private const int SonicTerrainGridMedium = 128;
    private const int SonicTerrainGridHigh = 160;
    private const int SonicTerrainGridUltra = 224;
    private const int SonicRippleCount = 10;
    private const int SonicMeteorCount = 20;
    private const int SonicMeteorTrailPerMeteor = 10;
    private const int SonicMeteorTrailParticleCount = SonicMeteorCount * SonicMeteorTrailPerMeteor;
    private const int SonicImpactSparkCount = 24;
    private const int SonicFloatingBlockCount = 80;
    private static readonly HttpClient TextureHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };
    private readonly Action<string> _log;
    private readonly bool _debugVisible;
    private readonly string _debugBackground;
    private readonly string _sonicDebugPass;
    private string _lastLoggedSonicBloomDebug = "";
    private ParticleVertex[] _vertices = Array.Empty<ParticleVertex>();
    private LyricSparkVertex[] _lyricSparks = Array.Empty<LyricSparkVertex>();
    private readonly NativeRipple[] _ripples = new NativeRipple[NativeRippleCount];
    private readonly NativeRipple[] _sonicRipples = new NativeRipple[SonicRippleCount];
    private readonly SonicMeteor[] _sonicMeteors = new SonicMeteor[SonicMeteorCount];
    private readonly SonicImpactSpark[] _sonicImpactSparks = new SonicImpactSpark[SonicImpactSparkCount];
    private readonly Random _random = new(431960);
    private int _coverStart;
    private int _coverCount;
    private int _genericStart;
    private int _genericCount;
    private int _jellyfishStart;
    private int _jellyfishCount;
    private int _maxDrawCount;
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGIFactory2 _factory;
    private IDXGISwapChain1 _swapChain;
    private IDCompositionDevice _compositionDevice;
    private IDCompositionTarget _compositionTarget;
    private IDCompositionVisual _compositionVisual;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _sonicSceneTexture;
    private ID3D11RenderTargetView? _sonicSceneTarget;
    private ID3D11ShaderResourceView? _sonicSceneView;
    private ID3D11Texture2D? _sonicEmissionTexture;
    private ID3D11RenderTargetView? _sonicEmissionTarget;
    private ID3D11ShaderResourceView? _sonicEmissionView;
    private ID3D11Texture2D? _sonicBloomHalfATexture;
    private ID3D11RenderTargetView? _sonicBloomHalfATarget;
    private ID3D11ShaderResourceView? _sonicBloomHalfAView;
    private ID3D11Texture2D? _sonicBloomHalfBTexture;
    private ID3D11RenderTargetView? _sonicBloomHalfBTarget;
    private ID3D11ShaderResourceView? _sonicBloomHalfBView;
    private ID3D11Texture2D? _sonicBloomQuarterATexture;
    private ID3D11RenderTargetView? _sonicBloomQuarterATarget;
    private ID3D11ShaderResourceView? _sonicBloomQuarterAView;
    private ID3D11Texture2D? _sonicBloomQuarterBTexture;
    private ID3D11RenderTargetView? _sonicBloomQuarterBTarget;
    private ID3D11ShaderResourceView? _sonicBloomQuarterBView;
    private ID3D11Texture2D? _sonicDepthBuffer;
    private ID3D11DepthStencilView? _sonicDepthView;
    private ID3D11VertexShader _vertexShader;
    private ID3D11GeometryShader _geometryShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11InputLayout _inputLayout;
    private ID3D11BlendState _blendState;
    private ID3D11BlendState _additiveBlendState;
    private ID3D11SamplerState _samplerState;
    private ID3D11ShaderResourceView? _coverView;
    private ID3D11ShaderResourceView? _edgeView;
    private ID3D11ShaderResourceView? _lyricView;
    private ID3D11Buffer _vertexBuffer;
    private ID3D11Buffer _constantBuffer;
    private ID3D11Buffer _lyricConstantBuffer;
    private ID3D11Buffer _lyricSparkBuffer;
    private ID3D11VertexShader _lyricVertexShader;
    private ID3D11PixelShader _lyricPixelShader;
    private ID3D11VertexShader _lyricSparkVertexShader;
    private ID3D11GeometryShader _lyricSparkGeometryShader;
    private ID3D11PixelShader _lyricSparkPixelShader;
    private ID3D11InputLayout _lyricSparkInputLayout;
    private ID3D11VertexShader _sonicVertexShader;
    private ID3D11PixelShader _sonicPixelShader;
    private ID3D11VertexShader _bloomVertexShader;
    private ID3D11PixelShader _bloomPixelShader;
    private ID3D11InputLayout _sonicInputLayout;
    private ID3D11Buffer _sonicCubeVertexBuffer = null!;
    private ID3D11Buffer _sonicCubeIndexBuffer = null!;
    private ID3D11Buffer _sonicColumnVertexBuffer = null!;
    private ID3D11Buffer _sonicColumnIndexBuffer = null!;
    private ID3D11Buffer _sonicDiscVertexBuffer = null!;
    private ID3D11Buffer _sonicDiscIndexBuffer = null!;
    private ID3D11Buffer _sonicConstantBuffer;
    private ID3D11Buffer _bloomConstantBuffer;
    private int _sonicCubeIndexCount;
    private int _sonicColumnIndexCount;
    private int _sonicDiscIndexCount;
    private string _loadedTexture = "";
    private readonly object _texturePrepareLock = new();
    private string _texturePreparingKey = "";
    private string _preparedTextureKey = "";
    private Bitmap? _preparedTextureBitmap;
    private byte[]? _preparedEdgePixels;
    private bool _preparedTextureFailed;
    private bool _texturePrepareWorkerRunning;
    private long _texturePrepareGeneration;
    private string _textureRequestedSource = "";
    private string _textureRequestedKey = "";
    private bool _textureRequestedNeedsEdge;
    private string _loadedLyricKey = "";
    private readonly object _lyricPrepareLock = new();
    private string _lyricPreparingKey = "";
    private string _preparedLyricKey = "";
    private Bitmap? _preparedLyricBitmap;
    private float _preparedLyricTextMin;
    private float _preparedLyricTextMax = 1f;
    private bool _preparedLyricFailed;
    private bool _lyricPrepareWorkerRunning;
    private long _lyricPrepareGeneration;
    private string _lyricRequestedKey = "";
    private WallpaperState? _lyricRequestedState;
    private volatile bool _disposed;
    private int _lyricTextureWidth = 1;
    private int _lyricTextureHeight = 1;
    private float _lyricTextMin = 0f;
    private float _lyricTextMax = 1f;
    private float _lyricMotionLift;
    private float _lyricMotionScale = 1f;
    private float _lyricMotionLastTime = float.NaN;
    private int _rippleIndex;
    private int _sonicRippleIndex;
    private int _sonicMeteorIndex;
    private int _sonicImpactSparkIndex;
    private int _lastLoggedSonicTerrainGrid = -1;
    private float _sonicLastMeteorAt = -10f;
    private bool _cursorInitialized;
    private Vector2 _lastCursor = new(-999f, -999f);
    private float _lastCursorRippleAt = -10f;
    private DesktopMouseWheelHook? _desktopMouseWheelHook;
    private int _pendingDesktopWheelDelta;
    private bool _desktopViewPrimaryDown;
    private bool _desktopViewDragging;
    private bool _desktopViewPressDragged;
    private Point _desktopViewLastPointer;
    private Point _desktopViewPressPointer;
    private Point _desktopViewLastClickPointer;
    private long _desktopViewLastClickTicks;
    private float _desktopViewRotationX;
    private float _desktopViewRotationY;
    private float _desktopViewVelocityX;
    private float _desktopViewVelocityY;
    private float _desktopViewZoomTarget;
    private float _desktopViewZoom;
    private float _desktopViewLastRenderTime;
    private Vector4 _desktopViewControl;
    private const float DesktopPointerSpinX = 0.0032f;
    private const float DesktopPointerSpinY = 0.0034f;
    private const float DesktopSpinMax = 6.2f;
    private const float DesktopSpinDamping = 0.90f;
    private float _lastSilkMusicRippleAt = -10f;
    private float _lastSilkMusicImpact;
    private int _silkMusicRippleIndex;
    private bool _lastSilkBassRising;
    private float _lastRenderTime;
    private bool _silkFadeInitialized;
    private float _silkFadeLevel;
    private float _silkLowMidPool;
    private float _silkFlowPool;
    private bool _macroInitialized;
    private int _macroPreset = -1;
    private float _macroStructureSmooth;
    private float _macroMotionSmooth;
    private float _macroGlossSmooth;
    private float _vinylSpin;
    private float _tunnelSpinClock;
    private float _tunnelFlowClock;
    private float _tunnelRingClock;
    private float _tunnelBreathClock;
    private float _tunnelSpeedSmooth;
    private const float FullTunnelEventPulseStrength = 1.28f;
    private const float MaxTunnelEventPulseStrength = 1.48f;
    private const float StrongOrbitMainWaveStrength = 1.16f;
    private const float ReservoirOrbitMainWaveStrength = 0.88f;
    private const float OrbitReservoirChargeScale = 0.62f;
    private const float OrbitMainWaveMinSpacing = 0.333f;
    private readonly float[] _tunnelEventPulseTravel = new float[2];
    private readonly float[] _tunnelEventPulseStrength = new float[2];
    private readonly float[] _tunnelEventPulseHold = new float[2];
    private float _tunnelEventPulseCooldown;
    private float _tunnelStrongPulseCooldown;
    private float _tunnelEventPulseSourcePrev;
    private float _tunnelEventPulseImpactPrev;
    private float _tunnelPendingPulseStrength;
    private float _tunnelPendingPulseAge;
    private bool _tunnelEnergyInitialized;
    private float _tunnelEnergyFast;
    private float _tunnelEnergySlow;
    private readonly float[] _galaxyBeatWaveAges = new float[GalaxyBeatWaveCount];
    private readonly float[] _galaxyBeatWaveStrengths = new float[GalaxyBeatWaveCount];
    private int _galaxyBeatWaveIndex;
    private bool _galaxyBeatWaveInitialized;
    private float _galaxyLastImpactAge = 4f;
    private float _galaxyLastPulse;
    private float _galaxyTimeSinceWave = 9f;
    private int _galaxyLastWaveSlot = -1;
    private float _orbitShellOpen;
    private float _orbitBreathPool;
    private float _orbitDirectBreath;
    private float _orbitFlowSource;
    private float _orbitFlowDetail;
    private float _orbitPrevImpact;
    private float _orbitSpinClock;
    private float _orbitDetailClock;
    private readonly float[] _orbitMainWaveTravel = new float[3];
    private readonly float[] _orbitMainWaveStrength = new float[3];
    private readonly float[] _orbitMainWaveBoost = new float[3];
    private readonly int[] _orbitMainWaveColor = new int[3];
    private float _orbitMainWaveCooldown;
    private float _orbitStrongWaveCooldown;
    private float _orbitReservoirCharge;
    private float _orbitWaveSourcePrev;
    private float _orbitReservoirLevelPrev;
    private bool _orbitReservoirArmed;
    private int _lastLoggedTexturePreset = -999;
    private int _width;
    private int _height;

    public D3DParticleRenderer(IntPtr hwnd, int width, int height, Action<string> log, bool debugVisible = false, string debugBackground = "dark", string sonicDebugPass = "")
    {
        _log = log;
        _debugVisible = debugVisible;
        _debugBackground = string.IsNullOrWhiteSpace(debugBackground) ? "dark" : debugBackground.Trim().ToLowerInvariant();
        _sonicDebugPass = NormalizeSonicDebugPass(sonicDebugPass);
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        BuildParticles();
        BuildLyricSparks();

        D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 },
            out _device,
            out _,
            out _context).CheckError();

        _factory = CreateDXGIFactory2<IDXGIFactory2>(false);
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget).CheckError();
        _compositionVisual = _compositionDevice.CreateVisual();
        _swapChain = CreateCompositionSwapChain();
        _compositionVisual.SetContent(_swapChain);
        _compositionTarget.SetRoot(_compositionVisual);
        _compositionDevice.Commit();

        var vsBlob = Compiler.Compile(VertexShaderSource, "main", "mineradio-particles-vs", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        var gsBlob = Compiler.Compile(GeometryShaderSource, "main", "mineradio-particles-gs", "gs_4_0", ShaderFlags.None, EffectFlags.None);
        var psBlob = Compiler.Compile(PixelShaderSource, "main", "mineradio-particles-ps", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        var lyricVsBlob = Compiler.Compile(LyricVertexShaderSource, "main", "mineradio-lyric-vs", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        var lyricPsBlob = Compiler.Compile(LyricPixelShaderSource, "main", "mineradio-lyric-ps", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        var lyricSparkVsBlob = Compiler.Compile(LyricSparkVertexShaderSource, "main", "mineradio-lyric-spark-vs", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        var lyricSparkGsBlob = Compiler.Compile(LyricSparkGeometryShaderSource, "main", "mineradio-lyric-spark-gs", "gs_4_0", ShaderFlags.None, EffectFlags.None);
        var lyricSparkPsBlob = Compiler.Compile(LyricSparkPixelShaderSource, "main", "mineradio-lyric-spark-ps", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        var sonicVsBlob = Compiler.Compile(SonicVertexShaderSource, "main", "mineradio-sonic-vs", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        var sonicPsBlob = Compiler.Compile(SonicPixelShaderSource, "main", "mineradio-sonic-ps", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        var bloomVsBlob = Compiler.Compile(SonicBloomVertexShaderSource, "main", "mineradio-sonic-bloom-vs", "vs_4_0", ShaderFlags.None, EffectFlags.None);
        var bloomPsBlob = Compiler.Compile(SonicBloomPixelShaderSource, "main", "mineradio-sonic-bloom-ps", "ps_4_0", ShaderFlags.None, EffectFlags.None);
        _vertexShader = _device.CreateVertexShader(vsBlob.Span);
        _geometryShader = _device.CreateGeometryShader(gsBlob.Span);
        _pixelShader = _device.CreatePixelShader(psBlob.Span);
        _lyricVertexShader = _device.CreateVertexShader(lyricVsBlob.Span);
        _lyricPixelShader = _device.CreatePixelShader(lyricPsBlob.Span);
        _lyricSparkVertexShader = _device.CreateVertexShader(lyricSparkVsBlob.Span);
        _lyricSparkGeometryShader = _device.CreateGeometryShader(lyricSparkGsBlob.Span);
        _lyricSparkPixelShader = _device.CreatePixelShader(lyricSparkPsBlob.Span);
        _sonicVertexShader = _device.CreateVertexShader(sonicVsBlob.Span);
        _sonicPixelShader = _device.CreatePixelShader(sonicPsBlob.Span);
        _bloomVertexShader = _device.CreateVertexShader(bloomVsBlob.Span);
        _bloomPixelShader = _device.CreatePixelShader(bloomPsBlob.Span);
        _inputLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("UV", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("RANDOM", 0, Format.R32_Float, 16, 0),
            new InputElementDescription("MODEL", 0, Format.R32G32B32_Float, 20, 0),
            new InputElementDescription("PART", 0, Format.R32_Float, 32, 0),
            new InputElementDescription("PARAM", 0, Format.R32G32B32A32_Float, 36, 0),
        }, vsBlob.Span);
        _lyricSparkInputLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("RANDOM", 0, Format.R32_Float, 8, 0),
            new InputElementDescription("LAYER", 0, Format.R32_Float, 12, 0),
        }, lyricSparkVsBlob.Span);
        _sonicInputLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("UV", 0, Format.R32G32_Float, 24, 0),
        }, sonicVsBlob.Span);

        _vertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            BindFlags = BindFlags.VertexBuffer,
            ByteWidth = (uint)(Marshal.SizeOf<ParticleVertex>() * Math.Max(1, _maxDrawCount)),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _constantBuffer = _device.CreateBuffer(new BufferDescription
        {
            BindFlags = BindFlags.ConstantBuffer,
            ByteWidth = (uint)Marshal.SizeOf<ParticleConstants>(),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _lyricConstantBuffer = _device.CreateBuffer(new BufferDescription
        {
            BindFlags = BindFlags.ConstantBuffer,
            ByteWidth = (uint)Marshal.SizeOf<LyricConstants>(),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _sonicConstantBuffer = _device.CreateBuffer(new BufferDescription
        {
            BindFlags = BindFlags.ConstantBuffer,
            ByteWidth = (uint)Marshal.SizeOf<SonicConstants>(),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _bloomConstantBuffer = _device.CreateBuffer(new BufferDescription
        {
            BindFlags = BindFlags.ConstantBuffer,
            ByteWidth = (uint)Marshal.SizeOf<SonicBloomConstants>(),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        CreateSonicGeometry();
        var lyricSparkStride = Marshal.SizeOf<LyricSparkVertex>();
        unsafe
        {
            fixed (LyricSparkVertex* src = _lyricSparks)
            {
                _lyricSparkBuffer = _device.CreateBuffer(new BufferDescription
                {
                    BindFlags = BindFlags.VertexBuffer,
                    ByteWidth = (uint)(lyricSparkStride * Math.Max(1, _lyricSparks.Length)),
                    Usage = ResourceUsage.Immutable,
                }, new SubresourceData((IntPtr)src, (uint)(lyricSparkStride * Math.Max(1, _lyricSparks.Length))));
            }
        }
        _blendState = _device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _additiveBlendState = _device.CreateBlendState(new BlendDescription(Blend.One, Blend.One));
        _samplerState = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        CreateRenderTarget();
        if (!_debugVisible)
        {
            _desktopMouseWheelHook = new DesktopMouseWheelHook((delta, point) =>
            {
                if (Native.IsDesktopInteractionPoint(point)) Interlocked.Add(ref _pendingDesktopWheelDelta, delta);
            });
            _log($"particle-3d-control enabled drag=player inertia={DesktopSpinDamping:0.00} wheel={_desktopMouseWheelHook.IsActive}");
        }
    }

    private static string NormalizeSonicDebugPass(string? pass)
    {
        return (pass ?? "").Trim().ToLowerInvariant() switch
        {
            "scene" or "scene-only" or "sceneonly" => "scene",
            "bright" or "bright-only" or "brightonly" => "bright",
            "bloom" or "bloom-only" or "bloomonly" => "bloom",
            "composite" or "final" or "final-composite" => "composite",
            _ => "",
        };
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        _renderTarget?.Dispose();
        _renderTarget = null;
        DisposeSonicBloomTargets();
        _sonicDepthView?.Dispose();
        _sonicDepthView = null;
        _sonicDepthBuffer?.Dispose();
        _sonicDepthBuffer = null;
        _swapChain.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateRenderTarget();
    }

    private IDXGISwapChain1 CreateCompositionSwapChain()
    {
        return _factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        }, null);
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _device.CreateRenderTargetView(backBuffer);
        CreateSonicDepthTarget();
        CreateSonicBloomTargets();
    }

    private void CreateSonicDepthTarget()
    {
        _sonicDepthView?.Dispose();
        _sonicDepthView = null;
        _sonicDepthBuffer?.Dispose();
        _sonicDepthBuffer = null;
        _sonicDepthBuffer = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _sonicDepthView = _device.CreateDepthStencilView(_sonicDepthBuffer);
    }

    private void CreateSonicBloomTargets()
    {
        DisposeSonicBloomTargets();
        var halfWidth = Math.Max(1, _width / 2);
        var halfHeight = Math.Max(1, _height / 2);
        var quarterWidth = Math.Max(1, _width / 4);
        var quarterHeight = Math.Max(1, _height / 4);
        CreateSonicBloomTarget(_width, _height, out _sonicSceneTexture, out _sonicSceneTarget, out _sonicSceneView);
        CreateSonicBloomTarget(_width, _height, out _sonicEmissionTexture, out _sonicEmissionTarget, out _sonicEmissionView);
        CreateSonicBloomTarget(halfWidth, halfHeight, out _sonicBloomHalfATexture, out _sonicBloomHalfATarget, out _sonicBloomHalfAView);
        CreateSonicBloomTarget(halfWidth, halfHeight, out _sonicBloomHalfBTexture, out _sonicBloomHalfBTarget, out _sonicBloomHalfBView);
        CreateSonicBloomTarget(quarterWidth, quarterHeight, out _sonicBloomQuarterATexture, out _sonicBloomQuarterATarget, out _sonicBloomQuarterAView);
        CreateSonicBloomTarget(quarterWidth, quarterHeight, out _sonicBloomQuarterBTexture, out _sonicBloomQuarterBTarget, out _sonicBloomQuarterBView);
    }

    private void CreateSonicBloomTarget(
        int width,
        int height,
        out ID3D11Texture2D texture,
        out ID3D11RenderTargetView target,
        out ID3D11ShaderResourceView view)
    {
        texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        target = _device.CreateRenderTargetView(texture);
        view = _device.CreateShaderResourceView(texture);
    }

    private void DisposeSonicBloomTargets()
    {
        _sonicSceneView?.Dispose();
        _sonicSceneTarget?.Dispose();
        _sonicSceneTexture?.Dispose();
        _sonicEmissionView?.Dispose();
        _sonicEmissionTarget?.Dispose();
        _sonicEmissionTexture?.Dispose();
        _sonicBloomHalfAView?.Dispose();
        _sonicBloomHalfATarget?.Dispose();
        _sonicBloomHalfATexture?.Dispose();
        _sonicBloomHalfBView?.Dispose();
        _sonicBloomHalfBTarget?.Dispose();
        _sonicBloomHalfBTexture?.Dispose();
        _sonicBloomQuarterAView?.Dispose();
        _sonicBloomQuarterATarget?.Dispose();
        _sonicBloomQuarterATexture?.Dispose();
        _sonicBloomQuarterBView?.Dispose();
        _sonicBloomQuarterBTarget?.Dispose();
        _sonicBloomQuarterBTexture?.Dispose();
        _sonicSceneView = null;
        _sonicSceneTarget = null;
        _sonicSceneTexture = null;
        _sonicEmissionView = null;
        _sonicEmissionTarget = null;
        _sonicEmissionTexture = null;
        _sonicBloomHalfAView = null;
        _sonicBloomHalfATarget = null;
        _sonicBloomHalfATexture = null;
        _sonicBloomHalfBView = null;
        _sonicBloomHalfBTarget = null;
        _sonicBloomHalfBTexture = null;
        _sonicBloomQuarterAView = null;
        _sonicBloomQuarterATarget = null;
        _sonicBloomQuarterATexture = null;
        _sonicBloomQuarterBView = null;
        _sonicBloomQuarterBTarget = null;
        _sonicBloomQuarterBTexture = null;
    }

    public void SaveBackBufferPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = backBuffer.Description;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.Usage = ResourceUsage.Staging;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(desc);
        _context.CopyResource(staging, backBuffer);
        _context.Flush();
        _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppPArgb);
            var rect = new Rectangle(0, 0, _width, _height);
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                unsafe
                {
                    var src = (byte*)mapped.DataPointer.ToPointer();
                    var dst = (byte*)data.Scan0.ToPointer();
                    var copyBytes = Math.Min((int)mapped.RowPitch, Math.Abs(data.Stride));
                    for (var y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * data.Stride, Math.Abs(data.Stride), copyBytes);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void BuildParticles()
    {
        var all = new List<ParticleVertex>(CoverParticleCount + GenericParticleCount + MaxModelParticleCount);
        _coverStart = all.Count;
        AddCoverGridParticles(all, CoverParticleGrid);
        _coverCount = all.Count - _coverStart;

        _genericStart = all.Count;
        AddCoverGridParticles(all, GenericParticleGrid);
        _genericCount = all.Count - _genericStart;

        // Preset 6 ("安魂"/skull) has been removed from the product route.
        // Keep the legacy loader method below for historical builds, but do not
        // allocate or draw skull particles in the active native wallpaper path.
        // Preset 7 (jellyfish) has also been removed from the product route.
        // Keep the legacy implementation below for historical builds, but do
        // not load its model asset or allocate its particle stream at startup.
        _jellyfishStart = _genericStart;
        _jellyfishCount = 0;

        _vertices = all.ToArray();
        _maxDrawCount = Math.Max(_coverCount, _genericCount);
        _log($"particle-stream cover={_coverCount} generic={_genericCount} skull=disabled jellyfish=disabled maxDraw={_maxDrawCount} total={_vertices.Length}");
    }

    private void AddCoverGridParticles(List<ParticleVertex> all, int grid)
    {
        var columns = grid;
        var rows = grid;
        var count = columns * rows;
        for (var i = 0; i < count; i++)
        {
            var x = i % columns;
            var y = i / columns;
            var u = (x + .5f) / columns;
            var v = (y + .5f) / rows;
            var px = columns > 1 ? x / (float)(columns - 1) : .5f;
            var py = rows > 1 ? y / (float)(rows - 1) : .5f;
            all.Add(new ParticleVertex
            {
                Position = new Vector2((px - .5f) * 2f, (py - .5f) * 2f),
                Uv = new Vector2(u, v),
                Random = (float)_random.NextDouble(),
                Model = Vector3.Zero,
                Part = -1f,
                Param = Vector4.Zero,
            });
        }
    }

    private void BuildLyricSparks()
    {
        var sparks = new LyricSparkVertex[LyricSparkCount];
        for (var i = 0; i < sparks.Length; i++)
        {
            var lane = (i + .5f) / Math.Max(1, sparks.Length);
            var jitterX = ((float)_random.NextDouble() - .5f) * .055f;
            var band = (float)_random.NextDouble();
            var above = i % 3 == 0 ? -1f : 1f;
            var y = (band - .5f) * .70f + above * (.10f + (float)_random.NextDouble() * .22f);
            sparks[i] = new LyricSparkVertex
            {
                Position = new Vector2(Frac(lane + jitterX), Math.Clamp(y, -.86f, .86f)),
                Random = (float)_random.NextDouble(),
                Layer = i % 5,
            };
        }
        _lyricSparks = sparks;
    }

    private void AddSkullParticles(List<ParticleVertex> all)
    {
        var path = ResolveAssetPath("skull-decimation-points.bin");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var floats = ReadFloat32File(path);
            var count = Math.Min(MaxModelParticleCount, floats.Length / 5);
            for (var i = 0; i < count; i++)
            {
                var offset = i * 5;
                var x = floats[offset];
                var y = floats[offset + 1];
                var z = floats[offset + 2];
                var kind = floats[offset + 3];
                var seed = floats[offset + 4];
                all.Add(new ParticleVertex
                {
                    Position = new Vector2(x * 0.58f, y * 0.62f + 0.08f),
                    Uv = new Vector2(Clamp01(x * 0.5f + 0.5f), Clamp01(0.5f - y * 0.42f)),
                    Random = Frac(seed * 0.001f),
                    Model = new Vector3(x, y, z),
                    Part = kind,
                    Param = new Vector4(kind, seed, 0f, 0f),
                });
            }
            _log($"asset-skull-points path={path} count={count}");
        }
        catch (Exception ex)
        {
            _log($"asset-skull-error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void AddJellyfishParticles(List<ParticleVertex> all)
    {
        var path = ResolveAssetPath("jellyfish-poly-google-points.bin");
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var floats = ReadFloat32File(path);
                var count = Math.Min(42000, floats.Length / 5);
                for (var i = 0; i < count; i++)
                {
                    var offset = i * 5;
                    var x = floats[offset];
                    var y = floats[offset + 1];
                    var z = floats[offset + 2];
                    var kind = floats[offset + 3];
                    var seed = floats[offset + 4];
                    all.Add(new ParticleVertex
                    {
                        Position = new Vector2(x * 0.62f, y * 0.42f + 0.06f),
                        Uv = new Vector2(Clamp01(x * 0.5f + 0.5f), Clamp01(0.5f - y * 0.32f)),
                        Random = Frac(seed * 0.001f),
                        Model = new Vector3(x, y, z),
                        Part = 6f,
                        Param = new Vector4(kind, 0f, 0f, 0f),
                    });
                }
                _log($"asset-jellyfish-model path={path} count={count}");
            }

            AddProceduralJellyfishParticles(all, Math.Max(0, MaxModelParticleCount - (all.Count - _jellyfishStart)));
        }
        catch (Exception ex)
        {
            _log($"asset-jellyfish-error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void AddProceduralJellyfishParticles(List<ParticleVertex> all, int budget)
    {
        if (budget <= 0) return;
        var before = all.Count;
        var jellyTotal = 5;
        for (var j = 0; j < jellyTotal && all.Count - before < budget; j++)
        {
            var main = j == 0;
            var density = main ? 1.0f : 0.44f;
            var capCount = (int)((main ? 2200 : 620) * density);
            var rimCount = (int)((main ? 720 : 260) * density);
            var oralCount = (int)((main ? 900 : 320) * density);
            var dustCount = (int)((main ? 420 : 180) * density);
            var tentacleStrands = main ? 24 : 10;
            var tentacleSteps = main ? 52 : 34;
            var ribStrands = main ? 26 : 10;
            var ribSteps = main ? 38 : 24;
            for (var i = 0; i < capCount && all.Count - before < budget; i++) AddJellyVertex(all, j, 0, RandAngle(), MathF.Sqrt(Rand()), Rand(), Rand());
            for (var i = 0; i < rimCount && all.Count - before < budget; i++) AddJellyVertex(all, j, 1, RandAngle(), Rand(), Rand(), Rand());
            for (var i = 0; i < oralCount && all.Count - before < budget; i++) AddJellyVertex(all, j, 3, MathF.Floor(Rand() * (main ? 18 : 10)) / (main ? 18 : 10), Rand(), Rand(), Rand());
            for (var rib = 0; rib < ribStrands && all.Count - before < budget; rib++)
            {
                var ribN = (rib + .5f) / ribStrands;
                for (var step = 0; step < ribSteps && all.Count - before < budget; step++) AddJellyVertex(all, j, 7, ribN, ribSteps > 1 ? step / (float)(ribSteps - 1) : 0f, Rand(), Rand());
            }
            for (var strand = 0; strand < tentacleStrands && all.Count - before < budget; strand++)
            {
                var strandN = (strand + .5f) / tentacleStrands;
                var rootR = .58f + Rand() * .66f;
                for (var step = 0; step < tentacleSteps && all.Count - before < budget; step++) AddJellyVertex(all, j, 4, strandN, tentacleSteps > 1 ? step / (float)(tentacleSteps - 1) : 0f, rootR, Rand());
            }
            for (var i = 0; i < dustCount && all.Count - before < budget; i++) AddJellyVertex(all, j, 5, Rand(), Rand(), Rand(), Rand());
        }
        _log($"asset-jellyfish-procedural count={all.Count - before}");
    }

    private void AddJellyVertex(List<ParticleVertex> all, int jellyId, float part, float x, float y, float z, float w)
    {
        var seed = Rand() * 1000f;
        var lane = (jellyId + MathF.Min(.999f, x / MathF.Max(1f, MathF.Tau))) / 5f;
        all.Add(new ParticleVertex
        {
            Position = Vector2.Zero,
            Uv = new Vector2(Clamp01(lane), Clamp01((part + y) / 8f)),
            Random = Frac(seed * 0.001f),
            Model = Vector3.Zero,
            Part = part,
            Param = new Vector4(jellyId, x, y, z + w * 0.001f),
        });
    }

    private float Rand() => (float)_random.NextDouble();
    private float RandAngle() => Rand() * MathF.Tau;

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
    private static float Frac(float value) => value - MathF.Floor(value);

    private static float[] ReadFloat32File(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = bytes.Length / sizeof(float);
        var values = new float[count];
        Buffer.BlockCopy(bytes, 0, values, 0, count * sizeof(float));
        return values;
    }

    private static string ResolveAssetPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var direct = Path.Combine(dir.FullName, "public", "assets", fileName);
            if (File.Exists(direct)) return direct;
            var appPublic = Path.Combine(dir.FullName, "resources", "app", "public", "assets", fileName);
            if (File.Exists(appPublic)) return appPublic;
            dir = dir.Parent;
        }
        return "";
    }

    public void Render(WallpaperState state, float time)
    {
        if (_renderTarget == null) return;
        try
        {
            var viewDt = _desktopViewLastRenderTime > 0f
                ? Math.Clamp(time - _desktopViewLastRenderTime, 0f, 0.06f)
                : 1f / 60f;
            _desktopViewLastRenderTime = time;
            _desktopViewControl = UpdateDesktopParticleViewControl(state, viewDt);
            if (state.Preset == 4)
            {
                RenderSonicPreset(state, time);
                return;
            }

            var (drawStart, drawCount) = ParticleRangeForPreset(state.Preset);
            _context.Map(_vertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var vertexMap).CheckError();
            unsafe
            {
                fixed (ParticleVertex* src = _vertices)
                {
                    var byteCount = Marshal.SizeOf<ParticleVertex>() * drawCount;
                    Buffer.MemoryCopy(src + drawStart, vertexMap.DataPointer.ToPointer(), Marshal.SizeOf<ParticleVertex>() * Math.Max(1, _maxDrawCount), byteCount);
                }
            }
            _context.Unmap(_vertexBuffer, 0);

            var dt = Math.Clamp(time - _lastRenderTime, 0f, 0.06f);
            _lastRenderTime = time;
            var silkFadeEnvelope = 1f;
            if (state.Preset == 0)
            {
                if (!_silkFadeInitialized)
                {
                    _silkFadeInitialized = true;
                    _silkFadeLevel = 0f;
                }
                var targetFade = state.Playing ? 1f : 0f;
                var fadeStep = dt / 5f;
                _silkFadeLevel = targetFade > _silkFadeLevel
                    ? MathF.Min(targetFade, _silkFadeLevel + fadeStep)
                    : MathF.Max(targetFade, _silkFadeLevel - fadeStep);
                silkFadeEnvelope = SmoothStep(0f, 1f, _silkFadeLevel);
                state.Opacity = Math.Clamp(state.Opacity * silkFadeEnvelope, 0f, 1f);
            }
            else
            {
                _silkFadeInitialized = false;
                _silkFadeLevel = 0f;
                _silkLowMidPool = 0f;
                _silkFlowPool = 0f;
            }
            var nativeImpactStrength = Math.Clamp(MathF.Max(state.VisualImpactStrength, state.VisualImpact), 0f, 1.4f);
            var tunnelSub = Math.Clamp(state.SubBass * state.VisualEqLowBreath, 0f, 1.8f);
            var tunnelBass = Math.Clamp(state.BassBody * state.VisualEqLowWeight, 0f, 1.8f);
            var tunnelLowMid = Math.Clamp(state.LowMid * state.VisualEqSlowFlow, 0f, 1.8f);
            var tunnelMidFlow = Math.Clamp(state.MidFlow * state.VisualEqMidDirection, 0f, 1.8f);
            var tunnelHighMid = Math.Clamp(state.HighMid * state.VisualEqEdgePeak, 0f, 1.8f);
            var tunnelPresence = Math.Clamp(state.Presence * state.VisualEqEdgePeak, 0f, 1.8f);
            var tunnelAir = Math.Clamp(state.Air * state.VisualEqAirDust, 0f, 1.8f);
            var tunnelKick = Math.Clamp(state.KickEnvelope * state.VisualEqKickSoft * state.VisualEqKickRelease, 0f, 1.9f);
            var shapedMacro = state.Preset == 1
                ? new VisualMacroValues(
                    tunnelSub,
                    tunnelMidFlow,
                    tunnelKick,
                    tunnelPresence
                )
                : ShapeMacroForPreset(
                    state.Preset,
                    new VisualMacroValues(state.VisualStructure, state.VisualMotion, nativeImpactStrength, state.VisualGloss)
                );
            if (!_macroInitialized || _macroPreset != state.Preset)
            {
                _macroInitialized = true;
                _macroPreset = state.Preset;
                _macroStructureSmooth = shapedMacro.Structure;
                _macroMotionSmooth = shapedMacro.Motion;
                _macroGlossSmooth = shapedMacro.Gloss;
            }
            else if (state.Preset == 1)
            {
                _macroStructureSmooth = SmoothMacro(_macroStructureSmooth, shapedMacro.Structure, dt, 0.045f, 0.30f, 0.24f);
                _macroMotionSmooth = SmoothMacro(_macroMotionSmooth, shapedMacro.Motion, dt, 0.050f, 0.34f, 0.28f);
                _macroGlossSmooth = SmoothMacro(_macroGlossSmooth, shapedMacro.Gloss, dt, 0.060f, 0.42f, 0.48f);
            }
            else
            {
                _macroStructureSmooth = SmoothMacro(_macroStructureSmooth, shapedMacro.Structure, dt, 0.090f, 0.80f, 0.72f);
                _macroMotionSmooth = SmoothMacro(_macroMotionSmooth, shapedMacro.Motion, dt, 0.110f, 0.92f, 0.84f);
                _macroGlossSmooth = SmoothMacro(_macroGlossSmooth, shapedMacro.Gloss, dt, 0.150f, 1.40f, 1.60f);
            }
            var macroStructure = Math.Clamp(_macroStructureSmooth, 0f, 1.2f);
            var macroMotion = Math.Clamp(_macroMotionSmooth, 0f, 1.2f);
            var macroImpact = Math.Clamp(shapedMacro.Impact, 0f, 1.2f);
            var macroGloss = Math.Clamp(_macroGlossSmooth, 0f, 1.2f);
            var macroEnergy = Math.Clamp(MathF.Max(state.Energy * 0.45f, macroStructure * 0.58f + macroMotion * 0.34f), 0f, 1.4f);
            var tunnelOuterAccent = 0f;
            var tunnelEventPulses = (Packed0: 0f, Packed1: 0f, Drag: 0f, Release: 0f);
            if (state.Preset == 1)
            {
                var tunnelEnergyDensity = Math.Clamp(
                    macroEnergy * 0.22f
                    + macroMotion * 0.34f
                    + macroStructure * 0.14f
                    + macroGloss * 0.12f
                    + tunnelPresence * 0.10f
                    + tunnelAir * 0.08f,
                    0f,
                    1.4f
                );
                if (!_tunnelEnergyInitialized)
                {
                    _tunnelEnergyInitialized = true;
                    _tunnelEnergyFast = tunnelEnergyDensity;
                    _tunnelEnergySlow = tunnelEnergyDensity;
                }
                else
                {
                    _tunnelEnergyFast = SmoothMacro(_tunnelEnergyFast, tunnelEnergyDensity, dt, 0.110f, 1.40f, 0.90f);
                    _tunnelEnergySlow = SmoothMacro(_tunnelEnergySlow, tunnelEnergyDensity, dt, 0.010f, 0.20f, 0.14f);
                }
                var normalRhythm = SmoothStep(
                    0.24f,
                    0.86f,
                    Math.Clamp(macroMotion * 0.34f + macroStructure * 0.08f + tunnelLowMid * 0.10f, 0f, 1.2f)
                );
                var liftDrive = Math.Clamp(
                    SmoothStep(0.055f, 0.28f, _tunnelEnergyFast - _tunnelEnergySlow) * 0.72f
                    + SmoothStep(0.54f, 0.96f, tunnelEnergyDensity) * 0.28f,
                    0f,
                    1.2f
                );
                var climaxDrive = SmoothStep(
                    0.62f,
                    1.08f,
                    Math.Clamp(macroImpact * 0.64f + tunnelKick * 0.24f + nativeImpactStrength * 0.24f, 0f, 1.4f)
                );
                var tunnelTempoTarget = Math.Clamp(normalRhythm * 0.42f + liftDrive * 0.42f + climaxDrive * 0.62f, 0f, 1.35f);
                _tunnelSpeedSmooth = SmoothMacro(_tunnelSpeedSmooth, tunnelTempoTarget, dt, 0.046f, 0.58f, 0.15f);
                var breathGroove = SmoothStep(
                    0.18f,
                    0.86f,
                    Math.Clamp(tunnelLowMid * 0.24f + tunnelMidFlow * 0.30f + macroMotion * 0.26f + macroStructure * 0.10f, 0f, 1.5f)
                );
                var breathImpact = SmoothStep(
                    0.08f,
                    0.70f,
                    Math.Clamp(macroImpact * 0.34f + tunnelKick * 0.36f + nativeImpactStrength * 0.12f, 0f, 1.4f)
                );
                var breathRhythm = Math.Clamp(breathImpact * 0.58f + breathGroove * 0.34f + normalRhythm * 0.24f + liftDrive * 0.22f, 0f, 1.35f);
                tunnelOuterAccent = Math.Clamp(
                    breathImpact * 0.50f
                    + liftDrive * 0.46f
                    + climaxDrive * 0.38f
                    + SmoothStep(0.48f, 0.92f, breathGroove) * normalRhythm * 0.22f,
                    0f,
                    1.35f
                );
                var visualSpeed = MathF.Max(0.08f, state.VisualSpeed);
                tunnelEventPulses = UpdateTunnelEventPulses(dt, visualSpeed, tunnelOuterAccent, breathImpact, liftDrive, breathRhythm);
                var pulseMotionScale = Math.Clamp(1f - tunnelEventPulses.Drag + tunnelEventPulses.Release, 0.84f, 1.09f);
                var spinRate = (0.014f + _tunnelSpeedSmooth * 0.052f + liftDrive * 0.018f + climaxDrive * 0.036f) * visualSpeed * pulseMotionScale;
                var flowRate = (0.010f + _tunnelSpeedSmooth * 0.032f + liftDrive * 0.012f + climaxDrive * 0.022f + tunnelAir * 0.003f) * visualSpeed * pulseMotionScale;
                var ringRate = (0.185f + _tunnelSpeedSmooth * 0.105f + breathGroove * 0.035f + breathImpact * 0.045f + climaxDrive * 0.030f) * visualSpeed;
                var breathRate = (0.128f + breathRhythm * 0.090f + liftDrive * 0.045f + climaxDrive * 0.075f) * MathF.Sqrt(visualSpeed);
                _tunnelSpinClock = Wrap(_tunnelSpinClock + dt * spinRate, MathF.Tau);
                _tunnelFlowClock = Wrap(_tunnelFlowClock + dt * flowRate, 1f);
                _tunnelRingClock = Wrap(_tunnelRingClock + dt * ringRate, 1f);
                _tunnelBreathClock = Wrap(_tunnelBreathClock + dt * breathRate, 1f);
            }
            else
            {
                _tunnelEnergyInitialized = false;
                _tunnelEnergyFast = 0f;
                _tunnelEnergySlow = 0f;
                _tunnelSpeedSmooth = SmoothMacro(_tunnelSpeedSmooth, 0f, dt, 0.050f, 0.30f, 0.24f);
                tunnelEventPulses = ResetTunnelEventPulses();
            }
            var audioA = new Vector4(state.SubBass, state.BassBody, state.LowMid, state.MidFlow);
            var audioB = new Vector4(state.HighMid, state.Presence, state.Brilliance, state.Air);
            var audioC = new Vector4(state.KickEnvelope, state.KickConfidence, state.VisualEqKickRelease, state.VisualBgFade);
            var galaxyPulseSource = Math.Clamp(MathF.Max(state.KickEnvelope, state.VisualImpactStrength), 0f, 1.4f);
            var galaxyWaves = state.Preset == 5
                ? UpdateGalaxyBeatWaves(state, dt, galaxyPulseSource)
                : ResetGalaxyBeatWaves();
            if (state.Preset == 0)
            {
                // Match the WebGL audio-uniform path: preset visual EQ should
                // shape the detailed bands before Silk consumes them.
                var silkBandSaturation = SmoothStep(
                    0.72f,
                    1.08f,
                    Math.Clamp(state.LowMid * 0.58f + state.MidFlow * 0.52f, 0f, 1.4f)
                );
                var silkFlowRaw = Math.Clamp(tunnelLowMid * 0.26f + tunnelMidFlow * 0.42f, 0f, 1.4f) * silkFadeEnvelope;
                var silkFlowRise = MathF.Max(0f, silkFlowRaw - _silkFlowPool);
                var silkPoolTarget = Math.Clamp(tunnelLowMid * 0.30f + tunnelMidFlow * 0.24f, 0f, 0.52f) * silkFadeEnvelope;
                var silkDetailTarget = Math.Clamp(
                    MathF.Max(SmoothStep(0.12f, 0.35f, silkFlowRise) * 0.32f, SmoothStep(0.86f, 1.18f, silkFlowRaw) * 0.18f),
                    0f,
                    0.34f
                ) * silkFadeEnvelope;
                _silkLowMidPool = SmoothMacro(_silkLowMidPool, silkPoolTarget, dt, 0.030f, 0.18f, 0.10f);
                _silkFlowPool = SmoothMacro(_silkFlowPool, silkDetailTarget, dt, 0.060f, 0.28f, 0.16f);
                var silkLowMid = Math.Clamp((tunnelLowMid * 0.14f + _silkLowMidPool * 0.86f) * (1f - silkBandSaturation * 0.32f), 0f, 0.36f);
                var silkMidFlow = Math.Clamp((tunnelMidFlow * 0.08f + _silkFlowPool * 0.92f) * (1f - silkBandSaturation * 0.38f), 0f, 0.22f);
                audioA = new Vector4(
                    tunnelSub * silkFadeEnvelope,
                    tunnelBass * silkFadeEnvelope,
                    silkLowMid * silkFadeEnvelope,
                    silkMidFlow * silkFadeEnvelope
                );
                audioB = new Vector4(
                    tunnelHighMid * silkFadeEnvelope,
                    tunnelPresence * silkFadeEnvelope,
                    Math.Clamp(state.Brilliance * state.VisualEqSparkle, 0f, 1.8f) * silkFadeEnvelope,
                    tunnelAir * silkFadeEnvelope
                );
                var silkTransient = Math.Clamp(
                    state.KickEnvelope * 0.52f
                    + state.VisualImpactPulse * 0.30f
                    + state.VisualImpactStrength * 0.18f,
                    0f,
                    1.15f
                ) * silkFadeEnvelope;
                audioC = new Vector4(
                    silkTransient,
                    Math.Clamp(state.KickConfidence, 0f, 1.2f),
                    state.VisualEqKickRelease,
                    state.VisualBgFade
                );
            }
            else if (state.Preset == 1)
            {
                audioA = new Vector4(
                    macroStructure,
                    tunnelBass,
                    tunnelLowMid,
                    macroMotion
                );
                audioB = new Vector4(
                    tunnelHighMid,
                    macroGloss,
                    tunnelEventPulses.Packed0,
                    tunnelAir
                );
                audioC = new Vector4(
                    macroImpact,
                    tunnelOuterAccent,
                    state.VisualEqKickRelease,
                    state.VisualBgFade
                );
            }
            else if (state.Preset == 5)
            {
                // Galaxy uses one broad beat pulse; keep detailed bands out of
                // the desktop shader so the field does not twitch per frequency.
                audioA = galaxyWaves.Ages;
                audioB = galaxyWaves.Strengths;
                audioC = new Vector4(
                    galaxyPulseSource,
                    Math.Clamp(state.KickConfidence, 0f, 1.2f),
                    state.VisualEqKickRelease,
                    state.VisualBgFade
                );
            }
            else if (state.Preset == 2)
            {
                var orbitAudible = state.Opacity > 0.08f;
                var shellTarget = orbitAudible ? 1f : 0f;
                _orbitShellOpen = SmoothMacro(_orbitShellOpen, shellTarget, dt, 0.020f, 0.20f, 0.20f);

                var orbitSub = Math.Clamp(state.SubBass * state.VisualEqLowBreath, 0f, 1.4f);
                var orbitBody = Math.Clamp(state.BassBody * state.VisualEqLowWeight, 0f, 1.4f);
                var orbitLowMid = Math.Clamp(state.LowMid * state.VisualEqSlowFlow, 0f, 1.3f);
                var orbitMidFlow = Math.Clamp(state.MidFlow * state.VisualEqMidDirection, 0f, 1.3f);
                var orbitKick = Math.Clamp(state.KickEnvelope * state.VisualEqKickSoft * state.VisualEqKickRelease * MathF.Max(0.35f, state.KickConfidence), 0f, 1.4f);
                var orbitVisualImpact = Math.Clamp(
                    state.VisualImpact * 0.36f
                    + state.VisualImpactStrength * 0.32f
                    + state.VisualImpactPulse * 0.20f
                    + orbitKick * 0.12f,
                    0f,
                    1.3f
                );
                var impactRise = MathF.Max(0f, MathF.Max(orbitKick, orbitVisualImpact) - _orbitPrevImpact);
                _orbitPrevImpact = SmoothMacro(_orbitPrevImpact, MathF.Max(orbitKick, orbitVisualImpact), dt, 0.22f, 3.2f, 1.6f);

                var directBreathTarget = Math.Clamp(
                    SmoothStep(0.30f, 0.76f, orbitKick) * 0.66f
                    + SmoothStep(0.040f, 0.20f, impactRise) * 0.56f
                    + SmoothStep(0.44f, 0.88f, orbitVisualImpact) * 0.36f,
                    0f,
                    0.92f
                ) * _orbitShellOpen;
                var reservoirTarget = Math.Clamp(
                    orbitSub * 0.25f
                    + orbitBody * 0.30f
                    + orbitLowMid * 0.13f
                    + orbitMidFlow * 0.16f
                    + orbitVisualImpact * 0.30f,
                    0f,
                    0.78f
                ) * _orbitShellOpen;
                _orbitDirectBreath = SmoothMacro(_orbitDirectBreath, directBreathTarget, dt, 0.30f, 2.10f, 1.32f);
                _orbitBreathPool = SmoothMacro(_orbitBreathPool, reservoirTarget, dt, 0.050f, 0.42f, 0.24f);
                var orbitWaveSource = Math.Clamp(
                    SmoothStep(0.36f, 0.78f, orbitKick) * 0.66f
                    + SmoothStep(0.055f, 0.22f, impactRise) * 0.72f
                    + SmoothStep(0.50f, 0.94f, orbitVisualImpact) * 0.34f,
                    0f,
                    1.22f
                ) * _orbitShellOpen;

                var flowRaw = Math.Clamp(orbitLowMid * 0.34f + orbitMidFlow * 0.56f + state.HighMid * 0.08f, 0f, 1.4f);
                var flowRise = MathF.Max(0f, flowRaw - _orbitFlowSource);
                _orbitFlowSource = SmoothMacro(_orbitFlowSource, flowRaw, dt, 0.10f, 1.2f, 0.70f);
                var flowDetailTarget = Math.Clamp(
                    MathF.Max(SmoothStep(0.055f, 0.22f, flowRise), SmoothStep(0.86f, 1.18f, flowRaw) * 0.38f),
                    0f,
                    1f
                ) * _orbitShellOpen;
                _orbitFlowDetail = SmoothMacro(_orbitFlowDetail, flowDetailTarget, dt, 0.16f, 1.15f, 0.78f);
                var orbitMainWaves = UpdateOrbitMainWaves(
                    dt,
                    MathF.Max(0.05f, state.VisualSpeed),
                    _orbitShellOpen,
                    orbitWaveSource,
                    _orbitBreathPool,
                    _orbitFlowDetail
                );
                _orbitSpinClock = Wrap(_orbitSpinClock + dt * (0.115f + _orbitFlowDetail * 0.030f + _orbitFlowSource * 0.018f) * MathF.Max(0.05f, state.VisualSpeed), MathF.Tau);
                _orbitDetailClock = Wrap(_orbitDetailClock + dt * (0.125f + _orbitFlowDetail * 0.12f + _orbitFlowSource * 0.065f + _orbitBreathPool * 0.035f) * MathF.Max(0.05f, state.VisualSpeed), 1f);

                audioA = new Vector4(
                    _orbitShellOpen,
                    Math.Clamp(_orbitDirectBreath * 1.42f, 0f, 0.96f),
                    _orbitFlowDetail,
                    Math.Clamp(_orbitFlowSource, 0f, 1.2f)
                );
                audioB = new Vector4(
                    Math.Clamp(state.HighMid * 0.12f, 0f, 0.4f),
                    Math.Clamp(state.Presence * 0.16f + state.Brilliance * 0.10f, 0f, 0.5f),
                    orbitMainWaves.Packed0,
                    orbitMainWaves.Packed1
                );
                audioC = new Vector4(
                    Math.Clamp(orbitKick * 0.30f + orbitVisualImpact * 0.18f, 0f, 0.42f),
                    orbitMainWaves.Packed2,
                    state.VisualEqKickRelease,
                    state.VisualBgFade
                );
            }
            if (state.Preset != 2)
            {
                ResetOrbitMainWaves();
            }
            var vinylBass = Math.Clamp(state.BassBody, 0f, 1.2f) * state.VisualEqLowWeight;
            var vinylFlow = Math.Clamp(state.MidFlow, 0f, 1.2f) * state.VisualEqMidDirection;
            var vinylSpeed = (0.36f + vinylFlow * 0.16f + vinylBass * 0.045f) * MathF.Max(0.05f, state.VisualSpeed);
            _vinylSpin = (_vinylSpin + dt * vinylSpeed) % MathF.Tau;
            var interaction = UpdateInteractionAndRipples(state, time, dt);
            var constants = new ParticleConstants
            {
                Time = time,
                Bass = state.Preset == 0 ? Math.Clamp(state.Bass * silkFadeEnvelope, 0f, 1.2f) : macroStructure,
                Mid = state.Preset == 0 ? Math.Clamp(state.Mid * silkFadeEnvelope, 0f, 1.2f) : macroMotion,
                Treble = state.Preset == 0 ? Math.Clamp(state.Treble * silkFadeEnvelope, 0f, 1.2f) : macroGloss,
                Beat = state.Preset == 0 ? Math.Clamp(state.Beat * silkFadeEnvelope, 0f, 1.4f) : macroImpact,
                Energy = state.Preset == 0 ? Math.Clamp(state.Energy * silkFadeEnvelope, 0f, 1.4f) : macroEnergy,
                Opacity = state.Opacity,
                Preset = state.Preset,
                Width = _width,
                Height = _height,
                UseTexture = LoadTextureIfNeeded(state.Texture, state.Preset, state.VisualEdge) ? 1f : 0f,
                Primary = state.Primary,
                Secondary = state.Secondary,
                Highlight = state.Highlight,
                VisualA = new Vector4(state.VisualIntensity, state.VisualSpeed, state.VisualPoint, state.VisualScatter),
                VisualB = new Vector4(state.VisualTwist, state.VisualDepth, state.VisualColor, state.VisualBloom),
                VisualC = new Vector4(state.VisualCinemaShake, state.VisualCinema ? 1f : 0f, state.VisualBloomEnabled ? 1f : 0f, state.VisualEdge ? 1f : 0f),
                VisualD = new Vector4(state.VisualEqLowBreath, state.VisualEqLowWeight, state.VisualEqSlowFlow, state.VisualEqMidDirection),
                VisualE = new Vector4(state.VisualEqEdgePeak, state.VisualEqSparkle, state.VisualEqAirDust, state.VisualEqKickSoft),
                AudioA = audioA,
                AudioB = audioB,
                AudioC = audioC,
                AudioD = new Vector4(
                    macroStructure,
                    macroMotion,
                    state.Preset == 1 ? _tunnelRingClock : state.VisualImpactPulse,
                    macroImpact
                ),
                Runtime = state.Preset == 1
                    ? new Vector4(_tunnelSpinClock, _tunnelFlowClock, _tunnelBreathClock, tunnelEventPulses.Packed1)
                    : state.Preset == 2
                        ? new Vector4(_orbitSpinClock, _orbitShellOpen, _orbitDetailClock, state.VisualCoverResolution)
                        : new Vector4(_vinylSpin, dt, state.VisualCoverGrid, state.VisualCoverResolution),
                Background = new Vector4(state.BackgroundLuma, state.BackgroundContrast, state.BackgroundBusy, state.BackgroundConfidence),
                Interaction = interaction,
                Ripple0 = RippleVector(0),
                Ripple1 = RippleVector(1),
                Ripple2 = RippleVector(2),
                Ripple3 = RippleVector(3),
                Ripple4 = RippleVector(4),
                Ripple5 = RippleVector(5),
                Ripple6 = RippleVector(6),
                Ripple7 = RippleVector(7),
                Ripple8 = RippleVector(8),
                Ripple9 = RippleVector(9),
                Ripple10 = RippleVector(10),
                Ripple11 = RippleVector(11),
                Hand = new Vector4(state.HandX, state.HandY, state.HandActive, state.GestureGrip),
                ViewControl = _desktopViewControl,
            };
            if (_lastLoggedTexturePreset != state.Preset)
            {
                _lastLoggedTexturePreset = state.Preset;
                _log($"render-preset preset={state.Preset} draw={drawCount} useTexture={constants.UseTexture:0} texture={TextureLogName(state.Texture)}");
            }
            _context.OMSetRenderTargets(_renderTarget);
            _context.RSSetViewport(0, 0, _width, _height);
            _context.ClearRenderTargetView(_renderTarget, DebugClearColor());
            _context.IASetPrimitiveTopology(PrimitiveTopology.PointList);
            _context.IASetInputLayout(_inputLayout);
            _context.IASetVertexBuffer(0, _vertexBuffer, (uint)Marshal.SizeOf<ParticleVertex>(), 0);
            _context.VSSetShader(_vertexShader);
            _context.VSSetConstantBuffer(0, _constantBuffer);
            _context.GSSetShader(_geometryShader);
            _context.GSSetConstantBuffer(0, _constantBuffer);
            _context.PSSetShader(_pixelShader);
            _context.PSSetConstantBuffer(0, _constantBuffer);
            BindPixelTexture();
            var solidSpherePreset = state.Preset == 2;
            if (state.VisualBloomEnabled && state.VisualBloom > 0.01f && state.Preset != 3 && !solidSpherePreset)
            {
                constants.Padding = 1f;
                UploadParticleConstants(constants);
                unsafe
                {
                    float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
                    _context.OMSetBlendState(_additiveBlendState, blendFactor, uint.MaxValue);
                }
                _context.Draw((uint)drawCount, 0);
            }
            constants.Padding = 0f;
            UploadParticleConstants(constants);
            unsafe
            {
                float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
                _context.OMSetBlendState(_blendState, blendFactor, uint.MaxValue);
            }
            _context.Draw((uint)drawCount, 0);
            _context.GSSetShader(null);
            RenderLyric(state, time);
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            _log($"render-error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private (int Start, int Count) ParticleRangeForPreset(int preset)
    {
        return preset switch
        {
            0 when _coverCount > 0 => (_coverStart, _coverCount),
            5 when _coverCount > 0 => (_coverStart, _coverCount),
            7 when _jellyfishCount > 0 => (_jellyfishStart, _jellyfishCount),
            _ => (_genericStart, _genericCount),
        };
    }

    private void UploadParticleConstants(ParticleConstants constants)
    {
        _context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cbMap).CheckError();
        Marshal.StructureToPtr(constants, cbMap.DataPointer, false);
        _context.Unmap(_constantBuffer, 0);
    }

    private static float SmoothMacro(float current, float target, float dt, float alpha60, float risePerSecond, float fallPerSecond)
    {
        var frameScale = MathF.Max(0.25f, Math.Clamp(dt, 1f / 240f, 0.12f) * 60f);
        var alpha = 1f - MathF.Pow(1f - Math.Clamp(alpha60, 0.001f, 0.95f), frameScale);
        var followed = current + (target - current) * alpha;
        var delta = followed - current;
        var limit = MathF.Max(0.001f, delta >= 0f ? risePerSecond : fallPerSecond) * MathF.Max(1f / 240f, dt);
        return current + Math.Clamp(delta, -limit, limit);
    }

    private static float Wrap(float value, float period)
    {
        if (period <= 0f) return value;
        value %= period;
        return value < 0f ? value + period : value;
    }

    private (Vector4 Ages, Vector4 Strengths) ResetGalaxyBeatWaves()
    {
        _galaxyBeatWaveInitialized = false;
        return (Vector4.Zero, Vector4.Zero);
    }

    private (float Packed0, float Packed1, float Drag, float Release) ResetTunnelEventPulses()
    {
        _tunnelEventPulseCooldown = 0f;
        _tunnelStrongPulseCooldown = 0f;
        _tunnelEventPulseSourcePrev = 0f;
        _tunnelEventPulseImpactPrev = 0f;
        _tunnelPendingPulseStrength = 0f;
        _tunnelPendingPulseAge = 0f;
        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            _tunnelEventPulseTravel[i] = 2f;
            _tunnelEventPulseStrength[i] = 0f;
            _tunnelEventPulseHold[i] = 0f;
        }
        return (0f, 0f, 0f, 0f);
    }

    private (float Packed0, float Packed1, float Drag, float Release) UpdateTunnelEventPulses(
        float dt,
        float visualSpeed,
        float source,
        float impactDrive,
        float liftDrive,
        float breathRhythm)
    {
        var sourceRise = source - _tunnelEventPulseSourcePrev;
        var impactRise = impactDrive - _tunnelEventPulseImpactPrev;
        _tunnelEventPulseSourcePrev = source;
        _tunnelEventPulseImpactPrev = impactDrive;
        _tunnelEventPulseCooldown = MathF.Max(0f, _tunnelEventPulseCooldown - dt);
        _tunnelStrongPulseCooldown = MathF.Max(0f, _tunnelStrongPulseCooldown - dt);
        var speedScale = MathF.Sqrt(MathF.Max(0.08f, visualSpeed));
        const float maxEventPulseStrength = MaxTunnelEventPulseStrength;
        const float pendingPulseScale = 0.58f;
        const float pendingPoolChargeScale = 0.72f;
        if (_tunnelPendingPulseStrength > 0.001f)
        {
            _tunnelPendingPulseAge += dt;
            if (_tunnelPendingPulseAge > 1.45f)
            {
                _tunnelPendingPulseStrength = MathF.Max(0f, _tunnelPendingPulseStrength - dt * 0.30f);
            }
        }
        else
        {
            _tunnelPendingPulseAge = 0f;
        }

        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            if (_tunnelEventPulseStrength[i] <= 0.01f) continue;
            var fullPulse = _tunnelEventPulseStrength[i] >= FullTunnelEventPulseStrength;
            var pulseSpeed = (0.62f + _tunnelEventPulseStrength[i] * 0.36f + impactDrive * 0.16f + liftDrive * 0.10f) * speedScale;
            if (fullPulse && _tunnelEventPulseHold[i] > 0f)
            {
                _tunnelEventPulseHold[i] = MathF.Max(0f, _tunnelEventPulseHold[i] - dt);
                continue;
            }
            if (fullPulse)
            {
                var normalizedTravel = Math.Clamp(_tunnelEventPulseTravel[i] / 0.94f, 0f, 1f);
                var afterBlueExit = SmoothStep(0.54f, 0.72f, normalizedTravel);
                var sharedOuterSpeed = 0.62f * speedScale * 0.58f;
                pulseSpeed = pulseSpeed + (sharedOuterSpeed - pulseSpeed) * afterBlueExit;
            }
            _tunnelEventPulseTravel[i] += dt * pulseSpeed;
            var strengthDecay = _tunnelEventPulseStrength[i] >= FullTunnelEventPulseStrength ? 0f : 0.08f;
            _tunnelEventPulseStrength[i] = MathF.Max(0f, _tunnelEventPulseStrength[i] - dt * strengthDecay);
            if (_tunnelEventPulseTravel[i] > 1.04f) _tunnelEventPulseStrength[i] = 0f;
        }

        var strongImpact = (impactDrive > 0.54f && impactRise > 0.060f) || (source > 0.86f && sourceRise > 0.045f);
        var strongPulseInOuterBand = false;
        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            var travel = _tunnelEventPulseTravel[i];
            var strength = _tunnelEventPulseStrength[i];
            if (strength >= 0.94f && travel > 0.50f && travel < 0.94f)
            {
                strongPulseInOuterBand = true;
                break;
            }
        }
        if (strongImpact && (_tunnelStrongPulseCooldown > 0f || strongPulseInOuterBand))
        {
            var deferredStrength = Math.Clamp(0.42f + source * 0.62f + MathF.Max(0f, sourceRise) * 1.10f + impactDrive * 0.26f + liftDrive * 0.08f, 0.35f, maxEventPulseStrength);
            var deferredCharge = deferredStrength * (0.40f + impactDrive * 0.18f + liftDrive * 0.08f) * pendingPoolChargeScale;
            _tunnelPendingPulseStrength = Math.Clamp(_tunnelPendingPulseStrength + deferredCharge, 0f, maxEventPulseStrength);
            _tunnelPendingPulseAge = 0f;
            _tunnelEventPulseCooldown = MathF.Max(_tunnelEventPulseCooldown, 0.10f);
            strongImpact = false;
        }
        var sectionLift = liftDrive > 0.58f && sourceRise > 0.030f;
        var accentRise = source > 0.30f && sourceRise > 0.075f;
        if (_tunnelEventPulseCooldown <= 0f && (strongImpact || sectionLift || accentRise))
        {
            var strength = Math.Clamp(0.32f + source * 0.62f + MathF.Max(0f, sourceRise) * 1.20f + impactDrive * 0.18f, 0.30f, maxEventPulseStrength);
            if (strongImpact)
            {
                strength = Math.Clamp(MathF.Max(strength, 1.10f + _tunnelPendingPulseStrength * 0.30f), 0.30f, maxEventPulseStrength);
                var startedNewPulse = AddTunnelEventPulse(strength, true, true, true);
                if (startedNewPulse)
                {
                    _tunnelStrongPulseCooldown = 0.50f;
                    _tunnelPendingPulseStrength = 0f;
                    _tunnelPendingPulseAge = 0f;
                }
                _tunnelEventPulseCooldown = 0.13f;
            }
            else
            {
                strength = Math.Clamp(strength, 0.30f, 0.92f);
                var accumulate = strength * pendingPulseScale * pendingPoolChargeScale * (0.88f + source * 0.14f + liftDrive * 0.10f + impactDrive * 0.08f);
                _tunnelPendingPulseStrength = Math.Clamp(_tunnelPendingPulseStrength + accumulate, 0f, maxEventPulseStrength);
                _tunnelPendingPulseAge = 0f;
                _tunnelEventPulseCooldown = 0.18f;
            }
        }
        if (_tunnelPendingPulseStrength >= FullTunnelEventPulseStrength && _tunnelEventPulseCooldown <= 0f)
        {
            if (_tunnelStrongPulseCooldown > 0f || strongPulseInOuterBand)
            {
                _tunnelPendingPulseAge = 0f;
            }
            else if (AddTunnelEventPulse(Math.Clamp(_tunnelPendingPulseStrength, FullTunnelEventPulseStrength, maxEventPulseStrength), true, true, true))
            {
                _tunnelStrongPulseCooldown = 0.50f;
                _tunnelEventPulseCooldown = 0.13f;
                _tunnelPendingPulseStrength = 0f;
                _tunnelPendingPulseAge = 0f;
            }
        }

        var drag = 0f;
        var release = 0f;
        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            var strength = _tunnelEventPulseStrength[i];
            if (strength <= 0.01f) continue;
            var travel = Math.Clamp(_tunnelEventPulseTravel[i], 0f, 1f);
            var bodyWindow = SmoothStep(0.05f, 0.48f, travel) * (1f - SmoothStep(0.70f, 0.94f, travel));
            var edgeWindow = SmoothStep(0.60f, 0.78f, travel) * (1f - SmoothStep(0.88f, 0.99f, travel));
            drag = MathF.Max(drag, strength * bodyWindow * (0.090f + breathRhythm * 0.030f));
            release = MathF.Max(release, strength * edgeWindow * (0.040f + impactDrive * 0.050f + liftDrive * 0.024f));
        }

        return (
            PackTunnelEventPulse(_tunnelEventPulseTravel[0], _tunnelEventPulseStrength[0]),
            PackTunnelEventPulse(_tunnelEventPulseTravel[1], _tunnelEventPulseStrength[1]),
            Math.Clamp(drag, 0f, 0.16f),
            Math.Clamp(release, 0f, 0.09f)
        );
    }

    private bool AddTunnelEventPulse(float strength, bool forceSeparate, bool allowNewWeakPulse, bool alignFullPulseToBlueExit = false)
    {
        if (!forceSeparate && (strength < 0.88f || !allowNewWeakPulse))
        {
            var mergeSlot = -1;
            var mergeScore = float.MaxValue;
            for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
            {
                var travel = _tunnelEventPulseTravel[i];
                var existingStrength = _tunnelEventPulseStrength[i];
                if (existingStrength <= 0.01f || travel < 0.018f || travel > 0.56f || existingStrength > 1.04f) continue;
                var score = MathF.Abs(travel - 0.24f) + existingStrength * 0.18f;
                if (score >= mergeScore) continue;
                mergeScore = score;
                mergeSlot = i;
            }
            if (mergeSlot >= 0)
            {
                var travel = _tunnelEventPulseTravel[mergeSlot];
                _tunnelEventPulseTravel[mergeSlot] = Math.Clamp(travel * 0.82f + 0.022f, 0.020f, 0.46f);
                _tunnelEventPulseStrength[mergeSlot] = Math.Clamp(_tunnelEventPulseStrength[mergeSlot] + strength * 0.20f, 0f, 0.92f);
                return false;
            }
        }
        if (!forceSeparate && !allowNewWeakPulse)
        {
            return false;
        }

        var slot = -1;
        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            if (_tunnelEventPulseStrength[i] <= 0.01f || _tunnelEventPulseTravel[i] > 0.96f)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            var weakest = _tunnelEventPulseStrength[0] <= _tunnelEventPulseStrength[1] ? 0 : 1;
            var weakestTravel = _tunnelEventPulseTravel[weakest];
            var protectsOuterRing = weakestTravel > 0.52f && weakestTravel < 0.92f;
            if (protectsOuterRing || strength <= _tunnelEventPulseStrength[weakest] + 0.08f)
            {
                return false;
            }
            slot = weakest;
        }
        var holdAtBlueExit = alignFullPulseToBlueExit && strength >= FullTunnelEventPulseStrength;
        _tunnelEventPulseTravel[slot] = holdAtBlueExit ? 0.436f : 0f;
        _tunnelEventPulseStrength[slot] = strength;
        _tunnelEventPulseHold[slot] = holdAtBlueExit ? 0.10f : 0f;
        return true;
    }

    private void BoostActiveStrongTunnelPulse(float impactDrive, float liftDrive, float extraBoost = 0f)
    {
        var slot = -1;
        var bestScore = -1f;
        for (var i = 0; i < _tunnelEventPulseTravel.Length; i++)
        {
            var travel = _tunnelEventPulseTravel[i];
            var strength = _tunnelEventPulseStrength[i];
            if (strength < 0.94f || travel < 0.34f || travel > 0.96f) continue;
            var score = strength + SmoothStep(0.42f, 0.74f, travel) * 0.20f;
            if (score <= bestScore) continue;
            bestScore = score;
            slot = i;
        }
        if (slot < 0) return;

        var boost = 0.085f + impactDrive * 0.115f + liftDrive * 0.050f + extraBoost;
        _tunnelEventPulseStrength[slot] = Math.Clamp(_tunnelEventPulseStrength[slot] + boost, 0f, MaxTunnelEventPulseStrength);
        _tunnelStrongPulseCooldown = MathF.Max(_tunnelStrongPulseCooldown, 0.24f);
    }

    private static float PackTunnelEventPulse(float travel, float strength)
    {
        if (strength <= 0.01f || travel < 0f || travel >= 1f) return 0f;
        var strengthBucket = MathF.Floor(Math.Clamp(strength, 0f, MaxTunnelEventPulseStrength) * 100f);
        return strengthBucket + Math.Clamp(travel, 0f, 0.999f);
    }

    private void ResetOrbitMainWaves()
    {
        _orbitMainWaveCooldown = 0f;
        _orbitStrongWaveCooldown = 0f;
        _orbitReservoirCharge = 0f;
        _orbitWaveSourcePrev = 0f;
        _orbitReservoirLevelPrev = 0f;
        _orbitReservoirArmed = false;
        for (var i = 0; i < _orbitMainWaveTravel.Length; i++)
        {
            _orbitMainWaveTravel[i] = 2f;
            _orbitMainWaveStrength[i] = 0f;
            _orbitMainWaveBoost[i] = 0f;
            _orbitMainWaveColor[i] = 0;
        }
    }

    private (float Packed0, float Packed1, float Packed2) UpdateOrbitMainWaves(
        float dt,
        float visualSpeed,
        float shellOpen,
        float waveSource,
        float reservoirLevel,
        float flowDetail)
    {
        var sourceRise = waveSource - _orbitWaveSourcePrev;
        _orbitWaveSourcePrev = waveSource;
        var reservoirRise = reservoirLevel - _orbitReservoirLevelPrev;
        _orbitReservoirLevelPrev = reservoirLevel;
        _orbitMainWaveCooldown = MathF.Max(0f, _orbitMainWaveCooldown - dt);
        _orbitStrongWaveCooldown = MathF.Max(0f, _orbitStrongWaveCooldown - dt);
        var speedScale = MathF.Sqrt(MathF.Max(0.08f, visualSpeed));

        for (var i = 0; i < _orbitMainWaveTravel.Length; i++)
        {
            if (_orbitMainWaveStrength[i] <= 0.01f) continue;
            var pulseSpeed = 0.218f * speedScale;
            _orbitMainWaveTravel[i] += dt * pulseSpeed;
            _orbitMainWaveBoost[i] = MathF.Max(0.48f, _orbitMainWaveBoost[i] - dt * 0.38f);
            if (_orbitMainWaveTravel[i] > 1.035f) _orbitMainWaveStrength[i] = 0f;
        }

        var chargeRate = reservoirLevel * (0.34f + flowDetail * 0.10f) + MathF.Max(0f, sourceRise) * 0.18f;
        _orbitReservoirCharge = Math.Clamp(_orbitReservoirCharge + dt * chargeRate * OrbitReservoirChargeScale, 0f, 1.15f);
        if (shellOpen < 0.10f)
        {
            _orbitReservoirCharge = 0f;
            _orbitReservoirArmed = false;
            _orbitReservoirLevelPrev = 0f;
            return (0f, 0f, 0f);
        }

        var strongTrigger = shellOpen > 0.58f
            && _orbitStrongWaveCooldown <= 0f
            && ((waveSource > 0.34f && sourceRise > 0.024f) || waveSource > 0.78f);
        if (strongTrigger && AddOrbitMainWave(StrongOrbitMainWaveStrength, true))
        {
            _orbitReservoirCharge = MathF.Max(0f, _orbitReservoirCharge - 0.34f);
            _orbitStrongWaveCooldown = 0.22f;
            _orbitMainWaveCooldown = MathF.Min(_orbitMainWaveCooldown, 0.08f);
        }

        var reservoirFull = shellOpen > 0.74f && _orbitReservoirCharge >= 0.58f;
        if (reservoirFull)
        {
            _orbitReservoirArmed = true;
        }
        else if (_orbitReservoirCharge < 0.44f)
        {
            _orbitReservoirArmed = false;
        }

        var reservoirEnergyRise = MathF.Max(MathF.Max(0f, sourceRise), MathF.Max(0f, reservoirRise));
        var reservoirReady = _orbitReservoirArmed
            && reservoirFull
            && !strongTrigger
            && _orbitMainWaveCooldown <= 0f
            && reservoirEnergyRise > 0.012f
            && CanSpawnOrbitMainWaveAtTop();
        if (reservoirReady && AddOrbitMainWave(ReservoirOrbitMainWaveStrength, false))
        {
            _orbitReservoirArmed = false;
            _orbitReservoirCharge = MathF.Max(0f, _orbitReservoirCharge - 0.52f);
            _orbitMainWaveCooldown = 0.34f;
        }

        return (
            PackOrbitMainWavePulse(_orbitMainWaveTravel[0], _orbitMainWaveStrength[0], _orbitMainWaveColor[0], _orbitMainWaveBoost[0]),
            PackOrbitMainWavePulse(_orbitMainWaveTravel[1], _orbitMainWaveStrength[1], _orbitMainWaveColor[1], _orbitMainWaveBoost[1]),
            PackOrbitMainWavePulse(_orbitMainWaveTravel[2], _orbitMainWaveStrength[2], _orbitMainWaveColor[2], _orbitMainWaveBoost[2])
        );
    }

    private bool CanSpawnOrbitMainWaveAtTop()
    {
        for (var i = 0; i < _orbitMainWaveTravel.Length; i++)
        {
            if (_orbitMainWaveStrength[i] > 0.01f && _orbitMainWaveTravel[i] < OrbitMainWaveMinSpacing)
            {
                return false;
            }
        }
        return true;
    }

    private bool AddOrbitMainWave(float strength, bool forceStrong)
    {
        if (!CanSpawnOrbitMainWaveAtTop())
        {
            return forceStrong && BoostActiveOrbitMainWave(strength, true);
        }

        var slot = -1;
        for (var i = 0; i < _orbitMainWaveTravel.Length; i++)
        {
            if (_orbitMainWaveStrength[i] <= 0.01f || _orbitMainWaveTravel[i] > 0.95f)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return BoostActiveOrbitMainWave(strength, forceStrong);
        }

        _orbitMainWaveTravel[slot] = 0f;
        _orbitMainWaveStrength[slot] = strength;
        _orbitMainWaveBoost[slot] = forceStrong ? 0.82f : 0.62f;
        _orbitMainWaveColor[slot] = forceStrong ? _random.Next(0, 5) : 0;
        return true;
    }

    private bool BoostActiveOrbitMainWave(float strength, bool forceStrong)
    {
        var slot = -1;
        var bestScore = -1f;
        for (var i = 0; i < _orbitMainWaveTravel.Length; i++)
        {
            var existingStrength = _orbitMainWaveStrength[i];
            var travel = _orbitMainWaveTravel[i];
            if (existingStrength <= 0.01f || travel < 0.035f || travel > 0.94f) continue;
            var score = existingStrength + SmoothStep(0.10f, 0.62f, travel) * 0.18f + (forceStrong ? 0.16f : 0f);
            if (score <= bestScore) continue;
            bestScore = score;
            slot = i;
        }
        if (slot < 0) return false;

        var strengthBoost = forceStrong ? 0.24f : 0.14f;
        _orbitMainWaveStrength[slot] = Math.Clamp(MathF.Max(_orbitMainWaveStrength[slot], strength * 0.92f) + strengthBoost, 0f, MaxTunnelEventPulseStrength);
        _orbitMainWaveBoost[slot] = MathF.Max(_orbitMainWaveBoost[slot], forceStrong ? 0.95f : 0.66f);
        _orbitMainWaveColor[slot] = _random.Next(0, 5);
        return true;
    }

    private static float PackOrbitMainWavePulse(float travel, float strength, int colorIndex, float boost)
    {
        if (strength <= 0.01f || travel < 0f || travel >= 1f) return 0f;
        var travelBucket = MathF.Floor(Math.Clamp(travel, 0f, 0.999f) * 2047f);
        var strengthBucket = MathF.Floor(Math.Clamp(strength, 0f, MaxTunnelEventPulseStrength) / MaxTunnelEventPulseStrength * 31f);
        var colorBucket = Math.Clamp(colorIndex, 0, 4);
        var boostBucket = MathF.Floor(Math.Clamp(boost, 0f, 0.99f) * 15f);
        return (((boostBucket * 8f + colorBucket) * 32f + strengthBucket) * 2048f) + travelBucket;
    }

    private (Vector4 Ages, Vector4 Strengths) UpdateGalaxyBeatWaves(WallpaperState state, float dt, float pulseSource)
    {
        var impactAge = Math.Clamp(state.VisualImpactAge, 0f, 4f);
        var kick = Math.Clamp(state.KickEnvelope * MathF.Max(0.35f, state.KickConfidence), 0f, 1.4f);
        var impact = Math.Clamp(MathF.Max(MathF.Max(state.VisualImpactPulse, state.VisualImpactStrength), state.VisualImpact), 0f, 1.2f);
        var pulse = Math.Clamp(
            (kick * 0.72f + impact * 0.28f) * (0.82f + Math.Clamp(state.VisualMotion, 0f, 1.2f) * 0.24f),
            0f,
            1.2f
        );

        if (!_galaxyBeatWaveInitialized)
        {
            _galaxyBeatWaveInitialized = true;
            _galaxyBeatWaveIndex = 0;
            _galaxyLastWaveSlot = -1;
            _galaxyTimeSinceWave = 9f;
            _galaxyLastImpactAge = impactAge;
            _galaxyLastPulse = pulse;
            for (var i = 0; i < GalaxyBeatWaveCount; i++)
            {
                _galaxyBeatWaveAges[i] = 9f;
                _galaxyBeatWaveStrengths[i] = 0f;
            }
            if (pulse > 0.42f) AddGalaxyBeatWave(MathF.Min(impactAge, 0.08f), pulse);
        }
        else
        {
            _galaxyTimeSinceWave += dt;
            for (var i = 0; i < GalaxyBeatWaveCount; i++)
            {
                if (_galaxyBeatWaveStrengths[i] <= 0f) continue;
                _galaxyBeatWaveAges[i] += dt;
                if (_galaxyBeatWaveAges[i] > 4.60f) _galaxyBeatWaveStrengths[i] = 0f;
            }

            var ageReset = impactAge < 0.14f && _galaxyLastImpactAge > 0.32f;
            var pulseRise = pulse > 0.42f && pulse > _galaxyLastPulse + 0.16f;
            if ((ageReset || pulseRise) && pulse > 0.42f && _galaxyTimeSinceWave > 0.28f)
            {
                AddGalaxyBeatWave(ageReset ? impactAge : MathF.Min(impactAge, 0.08f), pulse);
            }
            else if (pulse > 0.26f && _galaxyTimeSinceWave <= 0.28f)
            {
                BoostLatestGalaxyBeatWave(pulse * 0.16f);
            }

            _galaxyLastImpactAge = impactAge;
            _galaxyLastPulse = pulse;
        }

        return (
            new Vector4(_galaxyBeatWaveAges[0], _galaxyBeatWaveAges[1], _galaxyBeatWaveAges[2], _galaxyBeatWaveAges[3]),
            new Vector4(_galaxyBeatWaveStrengths[0], _galaxyBeatWaveStrengths[1], _galaxyBeatWaveStrengths[2], _galaxyBeatWaveStrengths[3])
        );
    }

    private void AddGalaxyBeatWave(float age, float strength)
    {
        _galaxyBeatWaveAges[_galaxyBeatWaveIndex] = Math.Clamp(age, 0f, 4.6f);
        _galaxyBeatWaveStrengths[_galaxyBeatWaveIndex] = Math.Clamp(strength, 0f, 1.2f);
        _galaxyLastWaveSlot = _galaxyBeatWaveIndex;
        _galaxyTimeSinceWave = 0f;
        _galaxyBeatWaveIndex = (_galaxyBeatWaveIndex + 1) % GalaxyBeatWaveCount;
    }

    private void BoostLatestGalaxyBeatWave(float amount)
    {
        if (_galaxyLastWaveSlot < 0 || _galaxyLastWaveSlot >= GalaxyBeatWaveCount) return;
        if (_galaxyBeatWaveStrengths[_galaxyLastWaveSlot] <= 0f) return;
        _galaxyBeatWaveStrengths[_galaxyLastWaveSlot] = Math.Clamp(_galaxyBeatWaveStrengths[_galaxyLastWaveSlot] + amount, 0f, 1.2f);
    }

    private readonly struct VisualMacroValues
    {
        public readonly float Structure;
        public readonly float Motion;
        public readonly float Impact;
        public readonly float Gloss;

        public VisualMacroValues(float structure, float motion, float impact, float gloss)
        {
            Structure = structure;
            Motion = motion;
            Impact = impact;
            Gloss = gloss;
        }
    }

    private static VisualMacroValues ShapeMacroForPreset(int preset, VisualMacroValues input)
    {
        return preset switch
        {
            0 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 1.04f, 0.92f),
                ShapeMacroValue(input.Motion, 0.34f, 1.50f),
                ShapeMacroValue(input.Impact, 0.30f, 1.18f),
                ShapeMacroValue(input.Gloss, 0.42f, 1.12f)),
            1 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.92f, 1.12f),
                ShapeMacroValue(input.Motion, 0.72f, 1.34f),
                ShapeMacroValue(input.Impact, 0.50f, 1.16f),
                ShapeMacroValue(input.Gloss, 0.42f, 1.15f)),
            2 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.88f, 1.12f),
                ShapeMacroValue(input.Motion, 0.48f, 1.42f),
                ShapeMacroValue(input.Impact, 0.42f, 1.18f),
                ShapeMacroValue(input.Gloss, 0.38f, 1.16f)),
            3 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.48f, 1.22f),
                ShapeMacroValue(input.Motion, 0.24f, 1.55f),
                ShapeMacroValue(input.Impact, 0.08f, 1.30f),
                ShapeMacroValue(input.Gloss, 0.46f, 1.08f)),
            4 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.62f, 1.14f),
                ShapeMacroValue(input.Motion, 0.54f, 1.36f),
                ShapeMacroValue(input.Impact, 0.40f, 1.18f),
                ShapeMacroValue(input.Gloss, 0.58f, 1.08f)),
            5 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.68f, 1.20f),
                ShapeMacroValue(input.Motion, 0.38f, 1.58f),
                ShapeMacroValue(input.Impact, 0.12f, 1.32f),
                ShapeMacroValue(input.Gloss, 0.56f, 1.10f)),
            6 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.64f, 1.16f),
                ShapeMacroValue(input.Motion, 0.26f, 1.48f),
                ShapeMacroValue(input.Impact, 0.24f, 1.22f),
                ShapeMacroValue(input.Gloss, 0.44f, 1.12f)),
            7 => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.74f, 1.10f),
                ShapeMacroValue(input.Motion, 0.66f, 1.24f),
                ShapeMacroValue(input.Impact, 0.34f, 1.18f),
                ShapeMacroValue(input.Gloss, 0.52f, 1.10f)),
            _ => new VisualMacroValues(
                ShapeMacroValue(input.Structure, 0.86f, 1.16f),
                ShapeMacroValue(input.Motion, 0.64f, 1.34f),
                ShapeMacroValue(input.Impact, 0.42f, 1.18f),
                ShapeMacroValue(input.Gloss, 0.50f, 1.12f)),
        };
    }

    private static float ShapeMacroValue(float value, float gain, float gamma)
    {
        var v = Math.Clamp(value * gain, 0f, 1.2f);
        return Math.Clamp(MathF.Pow(v, MathF.Max(0.20f, gamma)), 0f, 1.2f);
    }

    private Color4 DebugClearColor()
    {
        if (!_debugVisible) return new Color4(0f, 0f, 0f, 0f);
        return _debugBackground switch
        {
            "transparent" => new Color4(0f, 0f, 0f, 0f),
            "light" => new Color4(0.78f, 0.78f, 0.74f, 1f),
            "mid" => new Color4(0.36f, 0.37f, 0.38f, 1f),
            "bright" => new Color4(0.92f, 0.91f, 0.86f, 1f),
            _ => new Color4(0.015f, 0.018f, 0.024f, 1f),
        };
    }

    private Vector4 UpdateDesktopParticleViewControl(WallpaperState state, float dt)
    {
        if (_debugVisible) return Vector4.Zero;
        dt = Math.Clamp(dt, 1f / 240f, 0.06f);

        var hasPointer = Native.TryGetCursorPosition(out var pointer);
        var pointerOverDesktop = hasPointer && Native.IsDesktopInteractionPoint(pointer);
        var primaryDown = Native.IsPrimaryButtonDown();
        if (primaryDown && !_desktopViewPrimaryDown && pointerOverDesktop)
        {
            _desktopViewDragging = true;
            _desktopViewPressDragged = false;
            _desktopViewLastPointer = pointer;
            _desktopViewPressPointer = pointer;
            _desktopViewVelocityX = 0f;
            _desktopViewVelocityY = 0f;
        }
        else if (primaryDown && _desktopViewDragging && hasPointer)
        {
            var dx = pointer.X - _desktopViewLastPointer.X;
            var dy = pointer.Y - _desktopViewLastPointer.Y;
            if (Math.Abs(pointer.X - _desktopViewPressPointer.X) > 6 || Math.Abs(pointer.Y - _desktopViewPressPointer.Y) > 6)
            {
                _desktopViewPressDragged = true;
            }
            if (dx != 0 || dy != 0)
            {
                var rx = dy * DesktopPointerSpinX;
                var ry = dx * DesktopPointerSpinY;
                _desktopViewRotationX += rx;
                _desktopViewRotationY += ry;
                _desktopViewVelocityX = ClampDesktopSpin(rx / dt * 0.46f);
                _desktopViewVelocityY = ClampDesktopSpin(ry / dt * 0.46f);
                _desktopViewLastPointer = pointer;
            }
        }
        else if (!primaryDown && _desktopViewPrimaryDown)
        {
            if (_desktopViewDragging && !_desktopViewPressDragged && hasPointer)
            {
                var nowTicks = Environment.TickCount64;
                var closeToPreviousClick = Math.Abs(pointer.X - _desktopViewLastClickPointer.X) <= 8
                    && Math.Abs(pointer.Y - _desktopViewLastClickPointer.Y) <= 8;
                if (_desktopViewLastClickTicks > 0 && nowTicks - _desktopViewLastClickTicks <= 420 && closeToPreviousClick)
                {
                    ResetDesktopParticleView();
                    _desktopViewLastClickTicks = 0;
                }
                else
                {
                    _desktopViewLastClickTicks = nowTicks;
                    _desktopViewLastClickPointer = pointer;
                }
            }
            _desktopViewDragging = false;
        }
        _desktopViewPrimaryDown = primaryDown;

        // Match the player tick: direct drag rotates immediately, then the
        // captured angular velocity coasts with frame-rate-independent 0.90 damping.
        _desktopViewRotationX += _desktopViewVelocityX * dt;
        _desktopViewRotationY += _desktopViewVelocityY * dt;
        var damping = MathF.Pow(DesktopSpinDamping, dt * 60f);
        _desktopViewVelocityX *= damping;
        _desktopViewVelocityY *= damping;
        if (MathF.Abs(_desktopViewVelocityX) < 0.01f) _desktopViewVelocityX = 0f;
        if (MathF.Abs(_desktopViewVelocityY) < 0.01f) _desktopViewVelocityY = 0f;
        RebaseDesktopRotation(ref _desktopViewRotationX);
        RebaseDesktopRotation(ref _desktopViewRotationY);

        var wheelDelta = Interlocked.Exchange(ref _pendingDesktopWheelDelta, 0);
        if (wheelDelta != 0)
        {
            // Player DOM deltaY is -100 for one +120 Windows wheel notch.
            _desktopViewZoomTarget += -wheelDelta * (100f / 120f) * 0.005f;
        }
        var baselineRadius = DesktopCameraBaselineRadius(state.Preset);
        _desktopViewZoomTarget = Math.Clamp(_desktopViewZoomTarget, 2.4f - baselineRadius, 14f - baselineRadius);

        var rotationEase = 1f - MathF.Pow(0.945f, dt * 60f); // player per-frame lerp: 0.055
        var zoomEase = 1f - MathF.Pow(0.93f, dt * 60f);      // player camera-radius lerp: 0.07
        _desktopViewControl.X += (_desktopViewRotationX - _desktopViewControl.X) * rotationEase;
        _desktopViewControl.Y += (_desktopViewRotationY - _desktopViewControl.Y) * rotationEase;
        _desktopViewZoom += (_desktopViewZoomTarget - _desktopViewZoom) * zoomEase;
        _desktopViewControl.Z = _desktopViewZoom;
        _desktopViewControl.W = _desktopViewDragging ? 1f : 0f;
        return _desktopViewControl;
    }

    private void ResetDesktopParticleView()
    {
        _desktopViewRotationX = 0f;
        _desktopViewRotationY = 0f;
        _desktopViewVelocityX = 0f;
        _desktopViewVelocityY = 0f;
        _desktopViewZoomTarget = 0f;
    }

    private static float ClampDesktopSpin(float value)
    {
        return Math.Clamp(value, -DesktopSpinMax, DesktopSpinMax);
    }

    private static void RebaseDesktopRotation(ref float value)
    {
        if (MathF.Abs(value) < MathF.PI * 10f) return;
        value = MathF.IEEERemainder(value, MathF.Tau);
    }

    private static float DesktopCameraBaselineRadius(int preset)
    {
        return preset switch
        {
            1 => 6.2f,
            2 => 7.0f,
            3 => 8.0f,
            4 => 6.4f,
            5 => 9.4f,
            _ => 6.6f,
        };
    }

    private Vector4 UpdateInteractionAndRipples(WallpaperState state, float time, float dt)
    {
        for (var i = 0; i < _ripples.Length; i++)
        {
            if (_ripples[i].Strength <= 0.005f) continue;
            _ripples[i].Age += dt;
            if (_ripples[i].Age > 2f)
            {
                _ripples[i].Age = -10f;
                _ripples[i].Strength = 0f;
            }
        }

        UpdateSilkMusicRipples(state, time);

        var interaction = new Vector4(-999f, -999f, 0f, 0f);
        if (Native.TryGetCursorPosition(out var point))
        {
            var active = point.X >= 0 && point.Y >= 0 && point.X <= _width && point.Y <= _height;
            if (active)
            {
                var cursor = new Vector2(
                    point.X / Math.Max(1f, _width) * 2f - 1f,
                    1f - point.Y / Math.Max(1f, _height) * 2f);
                var movedDistance = _cursorInitialized ? Vector2.Distance(cursor, _lastCursor) : 0.18f;
                var moveDrive = Math.Clamp(movedDistance / 0.22f, 0.12f, 1f);
                interaction = new Vector4(cursor.X, cursor.Y, 1f, moveDrive);
                var moved = !_cursorInitialized || movedDistance > 0.12f;
                if (moved && time - _lastCursorRippleAt > 0.16f)
                {
                    var presetRippleScale = state.Preset switch
                    {
                        0 => 1.00f,
                        1 => 0.42f,
                        2 => 0.52f,
                        3 => 0.18f,
                        4 => 0.38f,
                        5 => 0.34f,
                        _ => 0.26f,
                    };
                    TriggerRipple(cursor, (0.26f + state.VisualStructure * 0.44f + state.VisualImpactPulse * 0.18f) * presetRippleScale);
                    _lastCursorRippleAt = time;
                }
                _lastCursor = cursor;
                _cursorInitialized = true;
            }
            else
            {
                _cursorInitialized = false;
            }
        }

        return interaction;
    }

    private void UpdateSilkMusicRipples(WallpaperState state, float time)
    {
        if (state.Preset != 0)
        {
            _lastSilkMusicImpact = 0f;
            _lastSilkBassRising = false;
            return;
        }

        var lowDrive = Math.Clamp(state.SubBass * 0.42f + state.BassBody * 0.50f + state.Bass * 0.34f, 0f, 1.6f);
        var bassDrive = Math.Clamp(MathF.Max(state.Bass, lowDrive * 0.72f), 0f, 1.6f);
        var impact = Math.Clamp(
            MathF.Max(state.KickEnvelope * MathF.Max(0.35f, state.KickConfidence), state.VisualImpactPulse * 0.76f)
            + lowDrive * 0.18f,
            0f,
            1.45f);
        var rising = impact - _lastSilkMusicImpact;
        _lastSilkMusicImpact = _lastSilkMusicImpact * 0.72f + impact * 0.28f;
        var isBassHit = bassDrive > 0.30f && !_lastSilkBassRising;
        _lastSilkBassRising = bassDrive > 0.225f;

        var strongHit = isBassHit || impact > 0.34f && rising > 0.055f;
        var sustainedHit = impact > 0.72f && time - _lastSilkMusicRippleAt > 0.62f;
        if (!strongHit && !sustainedHit) return;

        var sinceLast = time - _lastSilkMusicRippleAt;
        if (sinceLast < 0.18f) return;

        var burstIndex = _silkMusicRippleIndex++;
        var count = impact > 0.72f || SilkHash01(burstIndex * 13.17f + time * 0.37f) > 0.5f ? 3 : 2;
        for (var k = 0; k < count; k++)
        {
            var position = SilkRippleRegionPosition(burstIndex, k);
            var jitter = SilkHash01(burstIndex * 29.31f + k * 7.23f);
            var strength = Math.Clamp(0.045f + impact * 0.075f + lowDrive * 0.018f + jitter * 0.018f, 0f, 0.20f);
            TriggerRipple(position, strength);
        }
        _lastSilkMusicRippleAt = time;
    }

    private static Vector2 SilkRippleRegionPosition(int burstIndex, int slot)
    {
        const float planeSize = 4.8f;
        var regionIndex = Math.Abs(burstIndex * 5 + slot * 4) % 9;
        var rx = regionIndex % 3;
        var ry = regionIndex / 3;
        var x = (rx / 2f - 0.5f) * planeSize * 0.72f;
        var y = (ry / 2f - 0.5f) * planeSize * 0.72f;
        var seed = burstIndex * 41.7f + slot * 11.3f;
        x += (SilkHash01(seed) - 0.5f) * 0.7f;
        y += (SilkHash01(seed + 19.19f) - 0.5f) * 0.7f;
        return new Vector2(x, y);
    }

    private static float SilkHash01(float seed)
    {
        var v = MathF.Sin(seed) * 43758.5453f;
        return v - MathF.Floor(v);
    }

    private void TriggerRipple(Vector2 position, float strength)
    {
        var ripple = _ripples[_rippleIndex];
        ripple.Position = position;
        ripple.Age = 0f;
        ripple.Strength = Math.Clamp(strength, 0f, 1.7f);
        _ripples[_rippleIndex] = ripple;
        _rippleIndex = (_rippleIndex + 1) % _ripples.Length;
    }

    private Vector4 RippleVector(int index)
    {
        if (index < 0 || index >= _ripples.Length || _ripples[index].Strength <= 0.005f)
        {
            return new Vector4(0f, 0f, -10f, 0f);
        }
        var ripple = _ripples[index];
        return new Vector4(ripple.Position.X, ripple.Position.Y, ripple.Age, ripple.Strength);
    }

    private bool LoadTextureIfNeeded(string texture, int preset, bool visualEdge)
    {
        var textureKey = TextureCacheKey(texture);
        var needsEdge = preset == 0 && visualEdge;
        var requestKey = textureKey + (needsEdge ? "|edge=1" : "|edge=0");
        if (string.Equals(_loadedTexture, requestKey, StringComparison.OrdinalIgnoreCase))
        {
            CancelSupersededTexturePreparation();
            return _coverView != null;
        }
        if (string.IsNullOrWhiteSpace(textureKey))
        {
            CancelSupersededTexturePreparation();
            _coverView?.Dispose();
            _edgeView?.Dispose();
            _coverView = null;
            _edgeView = null;
            _loadedTexture = requestKey;
            return false;
        }

        Bitmap? prepared = null;
        byte[]? preparedEdgePixels = null;
        var prepareFailed = false;
        lock (_texturePrepareLock)
        {
            if (string.Equals(_preparedTextureKey, requestKey, StringComparison.OrdinalIgnoreCase))
            {
                prepared = _preparedTextureBitmap;
                preparedEdgePixels = _preparedEdgePixels;
                prepareFailed = _preparedTextureFailed;
                _preparedTextureBitmap = null;
                _preparedEdgePixels = null;
                _preparedTextureFailed = false;
                _preparedTextureKey = "";
            }
            else if (!string.Equals(_texturePreparingKey, requestKey, StringComparison.OrdinalIgnoreCase))
            {
                _texturePreparingKey = requestKey;
                _textureRequestedSource = texture;
                _textureRequestedKey = requestKey;
                _textureRequestedNeedsEdge = needsEdge;
                _texturePrepareGeneration++;
                if (!_texturePrepareWorkerRunning)
                {
                    _texturePrepareWorkerRunning = true;
                    _ = Task.Run(PrepareTextureLatestLoop);
                }
            }
        }
        if (prepareFailed)
        {
            _loadedTexture = requestKey;
            return _coverView != null;
        }
        // Keep the previous cover alive while the next one is decoded. This
        // keeps network/image work completely outside the render frame.
        if (prepared == null) return _coverView != null;

        ID3D11ShaderResourceView? nextCoverView = null;
        ID3D11ShaderResourceView? nextEdgeView = null;
        try
        {
            using var bitmap = prepared;
            var width = bitmap.Width;
            var height = bitmap.Height;
            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };
                var initialData = new SubresourceData(data.Scan0, (uint)data.Stride, (uint)(data.Stride * height));
                var texture2D = _device.CreateTexture2D(desc, initialData);
                nextCoverView = _device.CreateShaderResourceView(texture2D);
                texture2D.Dispose();
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            // The extra edge map belongs to the cover-grid preset only. Other
            // presets sample the cover texture directly and must not pay for it.
            nextEdgeView = needsEdge && preparedEdgePixels != null ? CreateEdgeTexture(preparedEdgePixels) : null;
            var previousCoverView = _coverView;
            var previousEdgeView = _edgeView;
            _coverView = nextCoverView;
            _edgeView = nextEdgeView;
            nextCoverView = null;
            nextEdgeView = null;
            _loadedTexture = requestKey;
            previousCoverView?.Dispose();
            previousEdgeView?.Dispose();
            _log($"texture-loaded source={TextureLogName(texture)} size={width}x{height}");
            return _coverView != null;
        }
        catch (Exception ex)
        {
            nextCoverView?.Dispose();
            nextEdgeView?.Dispose();
            _loadedTexture = requestKey;
            _log($"texture-load-error {TextureLogName(texture)} {ex.GetType().Name}: {ex.Message}");
            return _coverView != null;
        }
    }

    private void CancelSupersededTexturePreparation()
    {
        lock (_texturePrepareLock)
        {
            if (!_texturePrepareWorkerRunning && string.IsNullOrEmpty(_texturePreparingKey)) return;
            _texturePrepareGeneration++;
            _textureRequestedSource = "";
            _textureRequestedKey = "";
            _texturePreparingKey = "";
        }
    }

    private void PrepareTextureLatestLoop()
    {
        while (true)
        {
            string textureSource;
            string requestKey;
            bool needsEdge;
            long generation;
            lock (_texturePrepareLock)
            {
                if (_disposed)
                {
                    _texturePrepareWorkerRunning = false;
                    return;
                }
                textureSource = _textureRequestedSource;
                requestKey = _textureRequestedKey;
                needsEdge = _textureRequestedNeedsEdge;
                generation = _texturePrepareGeneration;
                if (string.IsNullOrEmpty(requestKey))
                {
                    _texturePrepareWorkerRunning = false;
                    return;
                }
            }

            Bitmap? prepared = null;
            byte[]? edgePixels = null;
            try
            {
                using var source = LoadTextureBitmap(textureSource);
                if (source != null)
                {
                    var width = Math.Max(1, Math.Min(2048, source.Width));
                    var height = Math.Max(1, Math.Min(2048, source.Height));
                    prepared = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                    using var graphics = Graphics.FromImage(prepared);
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                    if (needsEdge) edgePixels = BuildEdgeTexturePixels(prepared);
                }
            }
            catch (Exception ex)
            {
                _log($"texture-prepare-error {TextureLogName(textureSource)} {ex.GetType().Name}: {ex.Message}");
            }
            lock (_texturePrepareLock)
            {
                if (_disposed)
                {
                    prepared?.Dispose();
                    _texturePrepareWorkerRunning = false;
                    return;
                }
                if (generation != _texturePrepareGeneration)
                {
                    prepared?.Dispose();
                    continue;
                }
                _preparedTextureBitmap?.Dispose();
                _preparedTextureBitmap = prepared;
                _preparedEdgePixels = edgePixels;
                _preparedTextureFailed = prepared == null;
                _preparedTextureKey = requestKey;
                _texturePreparingKey = "";
                _texturePrepareWorkerRunning = false;
                return;
            }
        }
    }

    private void BindPixelTexture()
    {
        if (_coverView == null) return;
        var edgeView = _edgeView ?? _coverView;
        _context.VSSetShaderResources(0, 1, new[] { _coverView });
        _context.VSSetShaderResources(1, 1, new[] { edgeView });
        _context.VSSetSamplers(0, 1, new[] { _samplerState });
        _context.PSSetShaderResources(0, 1, new[] { _coverView });
        _context.PSSetShaderResources(1, 1, new[] { edgeView });
        _context.PSSetSamplers(0, 1, new[] { _samplerState });
    }

    private static byte[] BuildEdgeTexturePixels(Bitmap source)
    {
        const int W = 256;
        const int H = 256;
        const int N = W * H;
        using var bitmap = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, W, H));
        }

        var src = new byte[N * 4];
        var rect = new Rectangle(0, 0, W, H);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < H; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, src, y * W * 4, W * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var lum = new float[N];
        var alpha = new float[N];
        var chroma = new float[N];
        var blur = new float[N];
        var tmp = new float[N];
        for (var i = 0; i < N; i++)
        {
            var di = i * 4;
            var b = src[di];
            var g = src[di + 1];
            var r = src[di + 2];
            var a = src[di + 3] / 255f;
            alpha[i] = a;
            lum[i] = (r * .299f + g * .587f + b * .114f) / 255f;
            var max = Math.Max(r, Math.Max(g, b)) / 255f;
            var min = Math.Min(r, Math.Min(g, b)) / 255f;
            chroma[i] = max - min;
        }
        BlurHorizontal(lum, tmp, W, H, 4);
        BlurVertical(tmp, blur, W, H, 4);

        var edge = new float[N];
        for (var y = 1; y < H - 1; y++)
        {
            for (var x = 1; x < W - 1; x++)
            {
                var gx = -blur[(y - 1) * W + (x - 1)] - 2f * blur[y * W + (x - 1)] - blur[(y + 1) * W + (x - 1)]
                    + blur[(y - 1) * W + (x + 1)] + 2f * blur[y * W + (x + 1)] + blur[(y + 1) * W + (x + 1)];
                var gy = -blur[(y - 1) * W + (x - 1)] - 2f * blur[(y - 1) * W + x] - blur[(y - 1) * W + (x + 1)]
                    + blur[(y + 1) * W + (x - 1)] + 2f * blur[(y + 1) * W + x] + blur[(y + 1) * W + (x + 1)];
                edge[y * W + x] = Math.Min(1f, MathF.Sqrt(gx * gx + gy * gy) * 1.4f);
            }
        }

        var rgba = new byte[N * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var cx = (x / (float)(W - 1) - .5f) * 2f;
                var cy = (y / (float)(H - 1) - .5f) * 2f;
                var rr = MathF.Sqrt(cx * cx + cy * cy);
                var centerBias = 1f - Math.Min(1f, rr * .75f);
                var localDetail = MathF.Abs(lum[i] - blur[i]);
                var darkStructure = SmoothStep(.10f, .58f, (1f - lum[i]) * (edge[i] * .52f + chroma[i] * .46f + localDetail * .88f));
                var contentSignal = chroma[i] * .70f + edge[i] * .56f + localDetail * 1.12f + darkStructure * .34f;
                var content = SmoothStep(.045f, .42f, alpha[i] * contentSignal + edge[i] * .38f);
                var detail = SmoothStep(.050f, .36f, edge[i] * .74f + chroma[i] * .62f + localDetail * 1.20f + darkStructure * .18f);
                var depth = Math.Min(1f, (blur[i] * .34f + centerBias * .20f) * (.12f + content * .88f) + detail * .24f);
                var fg = Math.Min(1f, content * .62f + detail * .42f);
                var di = i * 4;
                rgba[di] = ToByte(depth);
                rgba[di + 1] = ToByte(edge[i]);
                rgba[di + 2] = ToByte(fg);
                rgba[di + 3] = ToByte(lum[i]);
            }
        }

        return rgba;
    }

    private ID3D11ShaderResourceView? CreateEdgeTexture(byte[] rgba)
    {
        const int W = 256;
        const int H = 256;
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription
            {
                Width = W,
                Height = H,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            };
            var initialData = new SubresourceData(handle.AddrOfPinnedObject(), W * 4u, W * H * 4u);
            var texture2D = _device.CreateTexture2D(desc, initialData);
            var view = _device.CreateShaderResourceView(texture2D);
            texture2D.Dispose();
            return view;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void BlurHorizontal(float[] src, float[] dst, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var sum = 0f;
            for (var x = -radius; x <= radius; x++) sum += src[y * width + Math.Clamp(x, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                dst[y * width + x] = sum / window;
                var xr = Math.Min(width - 1, x + radius + 1);
                var xl = Math.Max(0, x - radius);
                sum += src[y * width + xr] - src[y * width + xl];
            }
        }
    }

    private static void BlurVertical(float[] src, float[] dst, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var y = -radius; y <= radius; y++) sum += src[Math.Clamp(y, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                dst[y * width + x] = sum / window;
                var yd = Math.Min(height - 1, y + radius + 1);
                var yu = Math.Max(0, y - radius);
                sum += src[yd * width + x] - src[yu * width + x];
            }
        }
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static string TextureCacheKey(string texture)
    {
        var raw = texture.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return new Uri(raw).LocalPath;
            if (raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return $"data:{raw.Length}:{raw.GetHashCode()}";
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return raw;
        }
        catch {}
        return Path.IsPathFullyQualified(raw) ? raw : "";
    }

    private static Bitmap? LoadTextureBitmap(string texture)
    {
        var raw = texture.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(raw[(comma + 1)..]);
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            return new Bitmap(bitmap);
        }
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = TextureHttpClient.GetByteArrayAsync(raw).GetAwaiter().GetResult();
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            return new Bitmap(bitmap);
        }
        var filePath = raw;
        try
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) filePath = new Uri(raw).LocalPath;
        }
        catch {}
        if (!Path.IsPathFullyQualified(filePath) || !File.Exists(filePath)) return null;
        return new Bitmap(filePath);
    }

    private static string TextureLogName(string texture)
    {
        var raw = texture.Trim();
        if (raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return "data-image";
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(raw).Host; } catch { return "http-image"; }
        }
        try
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(new Uri(raw).LocalPath);
        }
        catch {}
        return Path.GetFileName(raw);
    }

    private void RenderLyric(WallpaperState state, float time)
    {
        var motionDelta = 1f / 60f;
        if (float.IsFinite(_lyricMotionLastTime))
        {
            var elapsed = time - _lyricMotionLastTime;
            if (elapsed > 0f && elapsed < .25f) motionDelta = elapsed;
        }
        _lyricMotionLastTime = time;
        if (!state.LyricEnabled || string.IsNullOrWhiteSpace(state.LyricText))
        {
            CancelSupersededLyricPreparation();
            _lyricMotionLift = 0f;
            _lyricMotionScale = 1f;
            return;
        }
        if (!LoadLyricTextureIfNeeded(state)) return;
        var aspect = _lyricTextureHeight > 0 ? (float)_lyricTextureWidth / _lyricTextureHeight : 4f;
        var widthNdc = Math.Clamp(1.36f * state.LyricScale, 0.35f, 1.92f);
        var heightNdc = Math.Clamp(widthNdc / Math.Max(1f, aspect) * (_width / Math.Max(1f, _height)), 0.06f, 0.36f);
        var cinemaBinding = state.Playing && state.LyricCinema;
        var motionBeat = cinemaBinding
            ? Math.Clamp(MathF.Max(state.Beat, MathF.Max(state.LyricGlowBeat ? state.LyricBeatGlow : 0f, state.VisualImpactPulse * .86f)), 0f, 1.35f)
            : 0f;
        var motionSolar = cinemaBinding ? Math.Clamp(state.LyricHighBloom, 0f, 1.45f) : 0f;
        var motionBass = cinemaBinding ? Math.Clamp(state.Bass, 0f, 1.2f) : 0f;
        var targetLift = cinemaBinding ? MathF.Min(22f, motionBeat * 18f + motionSolar * 5.2f + motionBass * 4.4f) : 0f;
        if (!state.Playing)
        {
            _lyricMotionLift = 0f;
            _lyricMotionScale = 1f;
        }
        else
        {
            var frameScale = Math.Clamp(motionDelta * 60f, .25f, 4f);
            var attackAlpha = 1f - MathF.Pow(1f - .46f, frameScale);
            var releaseAlpha = 1f - MathF.Pow(1f - .16f, frameScale);
            _lyricMotionLift += (targetLift - _lyricMotionLift) * (targetLift > _lyricMotionLift ? attackAlpha : releaseAlpha);
            var scaleTarget = 1f + motionBeat * .115f + motionSolar * .034f + motionBass * .026f;
            _lyricMotionScale += (scaleTarget - _lyricMotionScale) * (scaleTarget > _lyricMotionScale ? attackAlpha : releaseAlpha);
        }
        var floatY = state.Playing ? MathF.Sin(time * 1.08f) * -9.8f + MathF.Sin(time * 2.10f + .7f) * 3.1f : 0f;
        var floatX = state.Playing ? MathF.Sin(time * .70f + .4f) * 6.2f + MathF.Sin(time * 1.18f + 1.1f) * 2.6f : 0f;
        var bobY = floatY - _lyricMotionLift;
        var bobX = floatX + MathF.Sin(time * 1.55f) * motionBeat * 3.4f;
        var rotX = state.Playing ? MathF.Sin(time * .86f + .2f) * .65f - motionBeat * .18f : 0f;
        var rotY = state.Playing ? MathF.Sin(time * .74f + 1.3f) * -.55f + motionBeat * .08f : 0f;
        widthNdc = Math.Clamp(widthNdc * _lyricMotionScale, .35f, 2.08f);
        heightNdc = Math.Clamp(heightNdc * _lyricMotionScale, .06f, .42f);
        var centerX = state.LyricOffsetX * .38f + bobX * (2f / Math.Max(1f, _width));
        var centerY = -.06f - state.LyricOffsetY * .34f - bobY * (2f / Math.Max(1f, _height));
        var lyricDepthPulse = cinemaBinding
            ? Math.Clamp((state.LyricGlowBeat ? state.LyricBeatGlow * .16f : 0f) + state.LyricHighBloom * .10f + state.Bass * .035f, 0f, .34f)
            : 0f;
        var constants = new LyricConstants
        {
            CenterX = Math.Clamp(centerX, -.75f, .75f),
            CenterY = Math.Clamp(centerY, -.78f, .82f),
            Width = widthNdc,
            Height = heightNdc,
            Progress = Math.Clamp(state.LyricProgress, 0f, 1f),
            Opacity = state.LyricEnabled ? Math.Clamp(.76f + state.Beat * .10f + state.Bass * .06f + state.LyricHighBloom * .06f + (state.LyricGlowBeat ? state.LyricBeatGlow * .10f : 0f), .55f, 1f) : 0f,
            Beat = state.Beat,
            Bass = state.Bass,
            GlowStrength = state.LyricGlow ? state.LyricGlowStrength : 0f,
            HighBloom = state.LyricHighBloom,
            BeatGlow = state.LyricGlowBeat ? state.LyricBeatGlow : 0f,
            GlowParticles = state.LyricGlowParticles ? 1f : 0f,
            Time = time,
            Depth = Math.Clamp(state.LyricOffsetZ + lyricDepthPulse, -1.6f, 1.6f),
            TiltX = (state.LyricTiltX + rotX) * MathF.PI / 180f,
            TiltY = (state.LyricTiltY + rotY) * MathF.PI / 180f,
            CameraLock = state.LyricCameraLock ? 1f : 0f,
            Padding0 = state.LyricFeather,
            Padding1 = _lyricTextMin,
            Padding2 = _lyricTextMax,
            Primary = state.LyricPrimary,
            Highlight = state.LyricHighlight,
            Glow = state.LyricGlowColor,
            Background = new Vector4(state.BackgroundLuma, state.BackgroundContrast, state.BackgroundBusy, state.BackgroundConfidence),
        };
        _context.Map(_lyricConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var cbMap).CheckError();
        Marshal.StructureToPtr(constants, cbMap.DataPointer, false);
        _context.Unmap(_lyricConstantBuffer, 0);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        unsafe
        {
            float* blendFactor = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _context.OMSetBlendState(_blendState, blendFactor, uint.MaxValue);
        }
        _context.VSSetShader(_lyricVertexShader);
        _context.VSSetConstantBuffer(0, _lyricConstantBuffer);
        _context.PSSetShader(_lyricPixelShader);
        _context.PSSetConstantBuffer(0, _lyricConstantBuffer);
        _context.PSSetShaderResources(0, 1, new[] { _lyricView! });
        _context.PSSetSamplers(0, 1, new[] { _samplerState });
        _context.Draw(6, 0);
        RenderLyricSparks(state);
        _context.GSSetShader(null);
    }

    private void RenderLyricSparks(WallpaperState state)
    {
        if (!state.LyricEnabled || !state.LyricGlowParticles || state.LyricGlowStrength <= 0.01f || _lyricSparks.Length == 0) return;
        var stride = (uint)Marshal.SizeOf<LyricSparkVertex>();
        _context.IASetPrimitiveTopology(PrimitiveTopology.PointList);
        _context.IASetInputLayout(_lyricSparkInputLayout);
        _context.IASetVertexBuffer(0, _lyricSparkBuffer, stride, 0);
        _context.VSSetShader(_lyricSparkVertexShader);
        _context.VSSetConstantBuffer(0, _lyricConstantBuffer);
        _context.GSSetShader(_lyricSparkGeometryShader);
        _context.GSSetConstantBuffer(0, _lyricConstantBuffer);
        _context.PSSetShader(_lyricSparkPixelShader);
        _context.PSSetConstantBuffer(0, _lyricConstantBuffer);
        _context.Draw((uint)_lyricSparks.Length, 0);
    }

    private bool LoadLyricTextureIfNeeded(WallpaperState state)
    {
        var text = (state.LyricText ?? "").Trim();
        if (!state.LyricEnabled || string.IsNullOrWhiteSpace(text))
        {
            CancelSupersededLyricPreparation();
            return false;
        }
        var key = $"{text}|{state.LyricScale:F3}|{state.LyricWeight}|{state.LyricFont}|{_width}x{_height}";
        if (key == _loadedLyricKey)
        {
            CancelSupersededLyricPreparation();
            return _lyricView != null;
        }

        Bitmap? prepared = null;
        float preparedTextMin = 0f;
        float preparedTextMax = 1f;
        var prepareFailed = false;
        lock (_lyricPrepareLock)
        {
            if (_preparedLyricKey == key)
            {
                prepared = _preparedLyricBitmap;
                preparedTextMin = _preparedLyricTextMin;
                preparedTextMax = _preparedLyricTextMax;
                prepareFailed = _preparedLyricFailed;
                _preparedLyricBitmap = null;
                _preparedLyricFailed = false;
                _preparedLyricKey = "";
            }
            else if (_lyricPreparingKey != key)
            {
                _lyricPreparingKey = key;
                _lyricRequestedKey = key;
                _lyricRequestedState = state.Clone();
                _lyricPrepareGeneration++;
                if (!_lyricPrepareWorkerRunning)
                {
                    _lyricPrepareWorkerRunning = true;
                    _ = Task.Run(PrepareLyricLatestLoop);
                }
            }
        }
        if (prepareFailed)
        {
            _loadedLyricKey = key;
            return _lyricView != null;
        }
        // Keep the previous line until the new glyph bitmap is ready.
        if (prepared == null) return _lyricView != null;

        ID3D11ShaderResourceView? nextLyricView = null;
        try
        {
            using var bitmap = prepared;
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)bitmap.Width,
                    Height = (uint)bitmap.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };
                var initialData = new SubresourceData(data.Scan0, (uint)data.Stride, (uint)(data.Stride * bitmap.Height));
                var texture2D = _device.CreateTexture2D(desc, initialData);
                nextLyricView = _device.CreateShaderResourceView(texture2D);
                texture2D.Dispose();
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            var previousLyricView = _lyricView;
            _lyricView = nextLyricView;
            nextLyricView = null;
            _lyricTextureWidth = bitmap.Width;
            _lyricTextureHeight = bitmap.Height;
            _lyricTextMin = preparedTextMin;
            _lyricTextMax = preparedTextMax;
            _loadedLyricKey = key;
            previousLyricView?.Dispose();
            return _lyricView != null;
        }
        catch (Exception ex)
        {
            nextLyricView?.Dispose();
            _loadedLyricKey = key;
            _log($"lyric-texture-error {ex.GetType().Name}: {ex.Message}");
            return _lyricView != null;
        }
    }

    private void CancelSupersededLyricPreparation()
    {
        lock (_lyricPrepareLock)
        {
            if (!_lyricPrepareWorkerRunning && string.IsNullOrEmpty(_lyricPreparingKey)) return;
            _lyricPrepareGeneration++;
            _lyricRequestedKey = "";
            _lyricRequestedState = null;
            _lyricPreparingKey = "";
        }
    }

    private void PrepareLyricLatestLoop()
    {
        while (true)
        {
            string key;
            WallpaperState state;
            long generation;
            lock (_lyricPrepareLock)
            {
                if (_disposed || _lyricRequestedState == null)
                {
                    _lyricPrepareWorkerRunning = false;
                    return;
                }
                key = _lyricRequestedKey;
                state = _lyricRequestedState;
                generation = _lyricPrepareGeneration;
            }

            Bitmap? bitmap = null;
            var textMin = 0f;
            var textMax = 1f;
            try
            {
                bitmap = RenderLyricBitmap((state.LyricText ?? "").Trim(), state, out textMin, out textMax);
            }
            catch (Exception ex)
            {
                _log($"lyric-prepare-error {ex.GetType().Name}: {ex.Message}");
            }
            lock (_lyricPrepareLock)
            {
                if (_disposed)
                {
                    bitmap?.Dispose();
                    _lyricPrepareWorkerRunning = false;
                    return;
                }
                if (generation != _lyricPrepareGeneration)
                {
                    bitmap?.Dispose();
                    continue;
                }
                _preparedLyricBitmap?.Dispose();
                _preparedLyricBitmap = bitmap;
                _preparedLyricTextMin = textMin;
                _preparedLyricTextMax = textMax;
                _preparedLyricFailed = bitmap == null;
                _preparedLyricKey = key;
                _lyricPreparingKey = "";
                _lyricPrepareWorkerRunning = false;
                return;
            }
        }
    }

    private Bitmap RenderLyricBitmap(string text, WallpaperState state, out float textMin, out float textMax)
    {
        var rows = SplitLyricRows(text);
        var hasTranslation = rows.Length > 1;
        var width = Math.Max(512, Math.Min(2048, (int)(_width * .92f)));
        var heightRatio = hasTranslation ? .25f : .20f;
        var height = Math.Max(128, Math.Min(384, (int)(_height * heightRatio)));
        var fontName = LyricFontName(state.LyricFont);
        var style = state.LyricWeight >= 700 ? FontStyle.Bold : FontStyle.Regular;
        using var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var pg = Graphics.FromImage(probe);
        pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
        };
        var maxTextWidth = width * .91f;
        var originalRowHeight = hasTranslation ? height * .56f : height;
        var translationRowHeight = hasTranslation ? height * .31f : 0f;
        // LyricScale is already applied to the screen-space quad in RenderLyric.
        // Each row is fitted independently here, so neither the original lyric nor
        // the translation can auto-wrap into the other row.
        using var originalFont = CreateFittedLyricFont(
            pg,
            rows[0],
            fontName,
            style,
            hasTranslation ? Math.Clamp(height * .30f, 32f, 112f) : Math.Clamp(height * .43f, 32f, 128f),
            24f,
            maxTextWidth,
            originalRowHeight * .88f,
            format);
        using var translationFont = hasTranslation
            ? CreateFittedLyricFont(
                pg,
                rows[1],
                fontName,
                style,
                Math.Clamp(height * .20f, 24f, 76f),
                20f,
                maxTextWidth,
                translationRowHeight * .88f,
                format)
            : null;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var glow = new SolidBrush(System.Drawing.Color.FromArgb(86, System.Drawing.Color.White)))
        using (var fill = new SolidBrush(System.Drawing.Color.White))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var originalMeasured = Math.Clamp(g.MeasureString(rows[0], originalFont, PointF.Empty, format).Width, 1f, width);
            var widest = originalMeasured;
            if (hasTranslation && translationFont != null)
            {
                widest = Math.Max(widest, Math.Clamp(g.MeasureString(rows[1], translationFont, PointF.Empty, format).Width, 1f, width));
            }
            textMin = Math.Clamp((width - widest) * .5f / Math.Max(1f, width), 0f, .49f);
            textMax = Math.Clamp((width + widest) * .5f / Math.Max(1f, width), .51f, 1f);

            if (hasTranslation && translationFont != null)
            {
                var topPadding = height * .055f;
                var gap = height * .025f;
                var originalRect = new RectangleF(0, topPadding, width, originalRowHeight);
                var translationRect = new RectangleF(0, topPadding + originalRowHeight + gap, width, translationRowHeight);
                DrawLyricRow(g, rows[0], originalFont, originalRect, format, glow, fill);
                DrawLyricRow(g, rows[1], translationFont, translationRect, format, glow, fill);
            }
            else
            {
                DrawLyricRow(g, rows[0], originalFont, new RectangleF(0, 0, width, height), format, glow, fill);
            }
        }
        return bitmap;
    }

    private static string[] SplitLyricRows(string text)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (!normalized.Contains('\n'))
        {
            var legacySeparator = normalized.IndexOf(" ｜ ", StringComparison.Ordinal);
            if (legacySeparator > 0)
            {
                normalized = normalized[..legacySeparator] + "\n" + normalized[(legacySeparator + 3)..];
            }
        }
        var rawRows = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var original = rawRows.Length > 0 ? NormalizeLyricRow(rawRows[0]) : "";
        if (rawRows.Length < 2) return new[] { original };
        var translationParts = new List<string>();
        for (var i = 1; i < rawRows.Length; i++)
        {
            var part = NormalizeLyricRow(rawRows[i]);
            if (!string.IsNullOrWhiteSpace(part)) translationParts.Add(part);
        }
        var translation = string.Join(" ", translationParts);
        return string.IsNullOrWhiteSpace(translation) ? new[] { original } : new[] { original, translation };
    }

    private static string NormalizeLyricRow(string value)
    {
        return System.Text.RegularExpressions.Regex.Replace((value ?? "").Trim(), @"\s+", " ");
    }

    private static Font CreateFittedLyricFont(
        Graphics graphics,
        string text,
        string fontName,
        FontStyle style,
        float maxEm,
        float minEm,
        float maxWidth,
        float maxHeight,
        StringFormat format)
    {
        var em = maxEm;
        while (true)
        {
            var font = new Font(fontName, em, style, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(text, font, PointF.Empty, format);
            if ((measured.Width <= maxWidth && measured.Height <= maxHeight) || em <= minEm) return font;
            font.Dispose();
            em = Math.Max(minEm, em - 3f);
        }
    }

    private static void DrawLyricRow(
        Graphics graphics,
        string text,
        Font font,
        RectangleF layout,
        StringFormat format,
        Brush glow,
        Brush fill)
    {
        for (var radius = 10; radius >= 2; radius -= 2)
        {
            var alpha = Math.Max(10, 72 - radius * 5);
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(alpha, System.Drawing.Color.White));
            graphics.DrawString(text, font, brush, new RectangleF(
                layout.X - radius,
                layout.Y - radius,
                layout.Width + radius * 2,
                layout.Height + radius * 2), format);
        }
        graphics.DrawString(text, font, glow, new RectangleF(layout.X - 2, layout.Y - 2, layout.Width + 4, layout.Height + 4), format);
        graphics.DrawString(text, font, fill, layout, format);
    }

    private static string LyricFontName(string key)
    {
        return key switch
        {
            "song" => "SimSun",
            "kai" => "KaiTi",
            "mono" => "Consolas",
            _ => "Microsoft YaHei UI",
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _desktopMouseWheelHook?.Dispose();
        _desktopMouseWheelHook = null;
        lock (_texturePrepareLock)
        {
            _preparedTextureBitmap?.Dispose();
            _preparedTextureBitmap = null;
            _preparedEdgePixels = null;
        }
        lock (_lyricPrepareLock)
        {
            _preparedLyricBitmap?.Dispose();
            _preparedLyricBitmap = null;
        }
        _renderTarget?.Dispose();
        DisposeSonicBloomTargets();
        _sonicDepthView?.Dispose();
        _sonicDepthBuffer?.Dispose();
        _constantBuffer.Dispose();
        _lyricConstantBuffer.Dispose();
        _vertexBuffer.Dispose();
        _lyricSparkBuffer.Dispose();
        _blendState.Dispose();
        _additiveBlendState.Dispose();
        _samplerState.Dispose();
        _coverView?.Dispose();
        _edgeView?.Dispose();
        _lyricView?.Dispose();
        _inputLayout.Dispose();
        _lyricSparkInputLayout.Dispose();
        _sonicInputLayout.Dispose();
        _sonicConstantBuffer.Dispose();
        _bloomConstantBuffer.Dispose();
        _sonicCubeVertexBuffer.Dispose();
        _sonicCubeIndexBuffer.Dispose();
        _sonicColumnVertexBuffer.Dispose();
        _sonicColumnIndexBuffer.Dispose();
        _sonicDiscVertexBuffer.Dispose();
        _sonicDiscIndexBuffer.Dispose();
        _pixelShader.Dispose();
        _bloomPixelShader.Dispose();
        _sonicPixelShader.Dispose();
        _lyricPixelShader.Dispose();
        _lyricSparkPixelShader.Dispose();
        _lyricSparkGeometryShader.Dispose();
        _lyricSparkVertexShader.Dispose();
        _lyricVertexShader.Dispose();
        _geometryShader.Dispose();
        _bloomVertexShader.Dispose();
        _sonicVertexShader.Dispose();
        _vertexShader.Dispose();
        _compositionVisual.Dispose();
        _compositionTarget.Dispose();
        _compositionDevice.Dispose();
        _swapChain.Dispose();
        _factory.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private const string VertexShaderSource = @"
cbuffer Params : register(b0) {
  float uTime;
  float uBass;
  float uMid;
  float uTreble;
  float uBeat;
  float uEnergy;
  float uOpacity;
  float uPreset;
  float uWidth;
  float uHeight;
  float uUseTexture;
  float _pad0;
  float4 uPrimary;
  float4 uSecondary;
  float4 uHighlight;
  float4 uVisualA;
  float4 uVisualB;
  float4 uVisualC;
  float4 uVisualD;
  float4 uVisualE;
  float4 uAudioA;
  float4 uAudioB;
  float4 uAudioC;
  float4 uAudioD;
  float4 uRuntime;
  float4 uBackground;
  float4 uInteraction;
  float4 uRipple0;
  float4 uRipple1;
  float4 uRipple2;
  float4 uRipple3;
  float4 uRipple4;
  float4 uRipple5;
  float4 uRipple6;
  float4 uRipple7;
  float4 uRipple8;
  float4 uRipple9;
  float4 uRipple10;
  float4 uRipple11;
  float4 uHand;
  float4 uViewControl;
};
Texture2D uCover : register(t0);
Texture2D uEdge : register(t1);
SamplerState uCoverSampler : register(s0);
struct VSIn {
  float2 pos : POSITION;
  float2 uv : UV;
  float rnd : RANDOM;
  float3 model : MODEL;
  float part : PART;
  float4 param : PARAM;
};
struct VSOut {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float rnd : TEXCOORD1;
  float size : TEXCOORD2;
  float4 color : TEXCOORD3;
  float aux : TEXCOORD4;
  float depthInfo : TEXCOORD5;
  float viewDist : TEXCOORD6;
  float2 lineDir : TEXCOORD7;
  float beat : TEXCOORD8;
};
float hash(float n) { return frac(sin(n) * 43758.5453); }
float noise3(float3 x) {
  float3 p = floor(x);
  float3 f = frac(x);
  f = f * f * (3.0 - 2.0 * f);
  float n = p.x + p.y * 57.0 + p.z * 113.0;
  float a = lerp(hash(n + 0.0), hash(n + 1.0), f.x);
  float b = lerp(hash(n + 57.0), hash(n + 58.0), f.x);
  float c = lerp(hash(n + 113.0), hash(n + 114.0), f.x);
  float d = lerp(hash(n + 170.0), hash(n + 171.0), f.x);
  return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z) * 2.0 - 1.0;
}
float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 mod289v(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 perm(float4 x) { return mod289v(((x * 34.0) + 1.0) * x); }
float snoise(float3 v) {
  const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
  const float4 D = float4(0.0, 0.5, 1.0, 2.0);
  float3 i = floor(v + dot(v, C.yyy));
  float3 x0 = v - i + dot(i, C.xxx);
  float3 g = step(x0.yzx, x0.xyz);
  float3 l = 1.0 - g;
  float3 i1 = min(g.xyz, l.zxy);
  float3 i2 = max(g.xyz, l.zxy);
  float3 x1 = x0 - i1 + C.xxx;
  float3 x2 = x0 - i2 + C.yyy;
  float3 x3 = x0 - D.yyy;
  i = mod289(i);
  float4 p = perm(perm(perm(i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));
  float n_ = 0.142857142857;
  float3 ns = n_ * D.wyz - D.xzx;
  float4 j = p - 49.0 * floor(p * ns.z * ns.z);
  float4 x_ = floor(j * ns.z);
  float4 y_ = floor(j - 7.0 * x_);
  float4 x = x_ * ns.x + ns.yyyy;
  float4 y = y_ * ns.x + ns.yyyy;
  float4 h = 1.0 - abs(x) - abs(y);
  float4 b0 = float4(x.xy, y.xy);
  float4 b1 = float4(x.zw, y.zw);
  float4 s0 = floor(b0) * 2.0 + 1.0;
  float4 s1 = floor(b1) * 2.0 + 1.0;
  float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
  float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
  float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
  float3 p0 = float3(a0.xy, h.x);
  float3 p1 = float3(a0.zw, h.y);
  float3 p2 = float3(a1.xy, h.z);
  float3 p3 = float3(a1.zw, h.w);
  float4 norm = rsqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
  p0 *= norm.x;
  p1 *= norm.y;
  p2 *= norm.z;
  p3 *= norm.w;
  float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
  m = m * m;
  return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}
float rippleOne(float2 p, float4 rd) {
  float age = rd.z;
  float str = rd.w;
  if (str < 0.005 || age < 0.0 || age > 2.0) return 0.0;
  float dist = length(p - rd.xy);
  float lifeN = age / 2.0;
  float fadeIn = smoothstep(0.0, 0.06, age);
  float fadeOut = 1.0 - smoothstep(0.70, 1.0, lifeN);
  float env = fadeIn * fadeOut;
  float bulgeW = 0.38 + age * 0.58;
  float bulge = exp(-dist * dist / max(0.0008, 2.0 * bulgeW * bulgeW)) * (1.0 - smoothstep(0.0, 0.55, lifeN));
  float waveR = age * 1.58;
  float ringW = 0.24 + age * 0.15;
  float ring = exp(-pow((dist - waveR) / max(0.025, ringW), 2.0));
  return (bulge * 1.38 + ring * 0.78) * env * str;
}
float rippleSumAt(float2 p) {
  return rippleOne(p, uRipple0) + rippleOne(p, uRipple1) + rippleOne(p, uRipple2) + rippleOne(p, uRipple3) +
    rippleOne(p, uRipple4) + rippleOne(p, uRipple5) + rippleOne(p, uRipple6) + rippleOne(p, uRipple7) +
    rippleOne(p, uRipple8) + rippleOne(p, uRipple9) + rippleOne(p, uRipple10) + rippleOne(p, uRipple11);
}
float silkRippleOne(float2 p, float4 rd) {
  float age = rd.z;
  float str = rd.w;
  if (str < 0.005 || age < 0.0 || age > 2.0) return 0.0;
  float dist = length(p - rd.xy);
  float lifeN = age / 2.0;
  float fadeIn = smoothstep(0.0, 0.06, age);
  float fadeOut = 1.0 - smoothstep(0.70, 1.0, lifeN);
  float env = fadeIn * fadeOut;
  float bulgeW = 0.55 + age * 0.80;
  float bulge = exp(-dist * dist / max(0.0008, 2.0 * bulgeW * bulgeW)) * (1.0 - smoothstep(0.0, 0.55, lifeN));
  float waveR = age * 2.10;
  float ringW = 0.40 + age * 0.22;
  float ring = exp(-pow((dist - waveR) / max(0.025, ringW), 2.0));
  return (bulge * 2.4 + ring * 1.30) * env * str;
}
float silkRippleSumAt(float2 p) {
  return silkRippleOne(p, uRipple0) + silkRippleOne(p, uRipple1) + silkRippleOne(p, uRipple2) + silkRippleOne(p, uRipple3) +
    silkRippleOne(p, uRipple4) + silkRippleOne(p, uRipple5) + silkRippleOne(p, uRipple6) + silkRippleOne(p, uRipple7) +
    silkRippleOne(p, uRipple8) + silkRippleOne(p, uRipple9) + silkRippleOne(p, uRipple10) + silkRippleOne(p, uRipple11);
}
float tunnelPackedPulseStrength(float packed) {
  return floor(max(0.0, packed)) * 0.01;
}
float tunnelPackedPulseTravel(float packed) {
  return frac(max(0.0, packed));
}
float tunnelPackedPulseLife(float travel) {
  return smoothstep(0.0, 0.035, travel) * (1.0 - smoothstep(0.92, 1.0, travel));
}
float orbitPackedWaveTravel(float packed) {
  float whole = floor(max(0.0, packed) + 0.5);
  return fmod(whole, 2048.0) / 2047.0;
}
float orbitPackedWaveStrength(float packed) {
  float whole = floor(max(0.0, packed) + 0.5);
  float shifted = floor(whole / 2048.0);
  float strengthBucket = fmod(shifted, 32.0);
  return strengthBucket * (1.48 / 31.0);
}
float orbitPackedWaveColor(float packed) {
  float whole = floor(max(0.0, packed) + 0.5);
  float shifted = floor(whole / (2048.0 * 32.0));
  return fmod(shifted, 8.0);
}
float orbitPackedWaveBoost(float packed) {
  float whole = floor(max(0.0, packed) + 0.5);
  float shifted = floor(whole / (2048.0 * 32.0 * 8.0));
  return saturate(fmod(shifted, 16.0) / 15.0);
}
float orbitPackedWaveLife(float travel) {
  return smoothstep(0.0, 0.035, travel) * (1.0 - smoothstep(0.92, 1.0, travel));
}
float tunnelEventPulseRing(float packed, float band, float baseWidth) {
  float strength = tunnelPackedPulseStrength(packed);
  if (strength <= 0.01) return 0.0;
  float travel = tunnelPackedPulseTravel(packed);
  float fullStrength = smoothstep(1.24, 1.34, strength);
  float visibleTravel = saturate(travel / 0.94);
  float mappedTravel = lerp(saturate(travel), visibleTravel, fullStrength);
  float head = lerp(0.18, 1.00, mappedTravel);
  float exitBrightness = smoothstep(0.18, 0.56, head) * fullStrength;
  float width = max(0.010, baseWidth * (0.58 + strength * 0.08) * (1.0 - exitBrightness * 0.42));
  float fullGain = lerp(1.0, 0.42 + exitBrightness * 2.05, fullStrength);
  return exp(-pow((band - head) / width, 2.0)) * strength * tunnelPackedPulseLife(travel) * fullGain;
}
float galaxyBeatWaveOne(float pathCoord, float age, float strength) {
  if (strength < 0.010 || age < 0.0 || age > 4.60) return 0.0;
  float life = saturate(age / 4.40);
  float radius = lerp(0.01, 0.72, pow(life, 1.08));
  float width = lerp(0.045, 0.085, life);
  float delta = pathCoord - radius;
  float head = exp(-delta * delta / max(0.0001, width * width));
  float tailDelta = delta + width * 1.55;
  float tail = exp(-tailDelta * tailDelta / max(0.0001, width * width * 4.4))
    * (1.0 - smoothstep(0.0, width * 1.55, delta));
  float fade = smoothstep(0.0, 0.16, age) * (1.0 - smoothstep(3.60, 4.45, age));
  return strength * fade * (head * 0.86 + tail * 0.15);
}
float galaxyBeatWaveSum(float pathCoord) {
  float sum = 0.0;
  sum += galaxyBeatWaveOne(pathCoord, uAudioA.x, uAudioB.x);
  sum += galaxyBeatWaveOne(pathCoord, uAudioA.y, uAudioB.y);
  sum += galaxyBeatWaveOne(pathCoord, uAudioA.z, uAudioB.z);
  sum += galaxyBeatWaveOne(pathCoord, uAudioA.w, uAudioB.w);
  return min(sum, 1.35);
}
struct ProjectedPoint {
  float2 p;
  float viewZ;
  float clipDepth;
  float depthWeight;
};
ProjectedPoint projectWorld3(float3 world, float cameraRadius, float cameraPhi, float cameraTheta, float screenScale) {
  ProjectedPoint outp;
  float sx = sin(uViewControl.x);
  float cx = cos(uViewControl.x);
  float syView = sin(uViewControl.y);
  float cyView = cos(uViewControl.y);
  world = float3(world.x, world.y * cx - world.z * sx, world.y * sx + world.z * cx);
  world = float3(world.x * cyView + world.z * syView, world.y, -world.x * syView + world.z * cyView);
  cameraRadius = clamp(cameraRadius + uViewControl.z, 2.4, 14.0);
  float aspect = max(0.35, uWidth / max(1.0, uHeight));
  float cy = cos(cameraPhi);
  float sy = sin(cameraPhi);
  float ct = cos(cameraTheta);
  float st = sin(cameraTheta);
  float3 camPos = float3(cameraRadius * cy * st, cameraRadius * sy, cameraRadius * cy * ct);
  float3 forward = normalize(-camPos);
  float3 worldUp = float3(0.0, 1.0, 0.0);
  float3 right = normalize(cross(forward, worldUp));
  float3 cameraUp = normalize(cross(right, forward));
  float3 rel = world - camPos;
  float viewZ = -dot(rel, forward);
  float dist = max(0.65, -viewZ);
  float f = 2.41421356; // 45 degree perspective camera, same base FOV as the player.
  outp.p = float2(dot(rel, right) * f / aspect, dot(rel, cameraUp) * f) / dist * screenScale;
  outp.viewZ = -dist;
  outp.clipDepth = saturate((dist - 1.2) / 52.0);
  outp.depthWeight = saturate(1.0 - (dist - 2.0) / 42.0);
  return outp;
}
VSOut main(VSIn input) {
  VSOut o;
  o.color = float4(0.0, 0.0, 0.0, 0.0);
  o.aux = 0.0;
  o.beat = 0.0;
  float preset = floor(uPreset + 0.5);
  float2 base = input.pos;
  float2 p = base;
  float2 particleUv = input.uv;
  float2 structureLineDir = float2(0.0, 0.0);
  float structureLineWeight = 0.0;
  float clipDepth = 0.0;
  float depthScale = 1.0;
  float depthShade = 1.0;
  float particleViewZ = -26.0;
  float orbitPointBoost = 0.0;
  float intensity = max(0.08, uVisualA.x);
  float speed = max(0.08, uVisualA.y);
  float pointScale = max(0.18, uVisualA.z);
  float scatter = max(0.0, uVisualA.w);
  float twist = uVisualB.x;
  float bloomPass = saturate(_pad0);
  float depth = max(0.1, uVisualB.y);
  float cinemaShake = uVisualC.x * uVisualC.y;
  float lowBreath = uVisualD.x;
  float lowWeight = uVisualD.y;
  float slowFlow = uVisualD.z;
  float midDirection = uVisualD.w;
  float bgKnown5 = saturate(uBackground.w);
  float bgLuma5 = lerp(0.34, saturate(uBackground.x), bgKnown5);
  float bgConfidence5 = max(0.35, bgKnown5);
  float bgBright5 = smoothstep(0.46, 0.86, bgLuma5) * bgConfidence5;
  float bgDark5 = (1.0 - smoothstep(0.18, 0.42, bgLuma5)) * bgConfidence5;
  float galaxySizeFocus = 0.0;
  float edgePeak = uVisualE.x;
  float sparkle = uVisualE.y;
  float airDust = uVisualE.z;
  float kickSoft = max(0.05, uVisualE.w);
  float kickRelease = max(0.35, uAudioC.z);
  float sub = clamp(max(uAudioA.x * lowBreath, uBass * 0.18), 0.0, 1.35);
  float body = clamp(max(uAudioA.y * lowWeight, uBass * 0.28), 0.0, 1.35);
  float lowMid = clamp(max(uAudioA.z * slowFlow, uMid * 0.22), 0.0, 1.25);
  float midFlow = clamp(max(uAudioA.w * midDirection, uMid * 0.24), 0.0, 1.25);
  float high = clamp(max(uAudioB.x * edgePeak, uTreble * 0.14), 0.0, 1.25);
  float presence = clamp(max(uAudioB.y * edgePeak, uTreble * 0.18), 0.0, 1.25);
  float spark = clamp(max(max(uAudioB.z * sparkle, uAudioB.w * airDust), uTreble * 0.16), 0.0, 1.35);
  float kick = clamp(max(uAudioC.x * kickSoft * kickRelease, uBeat * 0.20), 0.0, 1.45);
  float air = clamp(max(uAudioB.w * airDust, uTreble * 0.14), 0.0, 1.25);
  if (preset == 0.0) {
    // Silk must consume the native reservoir-shaped bands. Falling back to
    // coarse uMid here bypasses the low/mid pool and makes the Z cloth wobble
    // as one fast slab when the song saturates the mid-low range.
    sub = clamp(uAudioA.x, 0.0, 1.10);
    body = clamp(uAudioA.y, 0.0, 1.10);
    lowMid = clamp(uAudioA.z, 0.0, 0.52);
    midFlow = clamp(uAudioA.w, 0.0, 0.34);
    high = clamp(uAudioB.x, 0.0, 0.82);
    presence = clamp(uAudioB.y, 0.0, 0.86);
    spark = clamp(uAudioB.z, 0.0, 0.54);
    kick = clamp(uAudioC.x, 0.0, 1.05);
    air = clamp(uAudioB.w, 0.0, 0.58);
  } else if (preset == 1.0) {
    // Tunnel keeps the player tube formula. Use midFlow for forward travel,
    // kickEnvelope for ring pulses, subBass for radius breathing, and presence
    // for ring highlights. Do not fall back to full bass/mid/beat/treble here.
    sub = clamp(uAudioA.x, 0.0, 1.35);
    body = clamp(uAudioA.y, 0.0, 1.35);
    lowMid = clamp(uAudioA.z, 0.0, 1.25);
    midFlow = clamp(uAudioA.w, 0.0, 1.25);
    high = clamp(uAudioB.x, 0.0, 1.25);
    presence = clamp(uAudioB.y, 0.0, 1.25);
    spark = 0.0;
    kick = clamp(uAudioC.x, 0.0, 1.45);
    air = clamp(uAudioB.w, 0.0, 1.25);
  } else if (preset == 2.0) {
    // Orbit should read as a rotating line planet: low bands shape the sphere,
    // midFlow rotates it, and kickEnvelope only accents the equator shock.
    sub = clamp(max(uAudioA.x, uBass * 0.16), 0.0, 1.35);
    body = clamp(max(uAudioA.y, uBass * 0.24), 0.0, 1.35);
    lowMid = clamp(max(uAudioA.z, uMid * 0.20), 0.0, 1.25);
    midFlow = clamp(max(uAudioA.w, uMid * 0.22), 0.0, 1.25);
    high = clamp(max(uAudioB.x * 0.38, uTreble * 0.07), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.10), 0.0, 1.25);
    spark = clamp(max(uAudioB.w * 0.24, uTreble * 0.05), 0.0, 1.35);
    kick = clamp(max(uAudioC.x * max(0.45, kickSoft) * max(0.55, kickRelease), uBeat * 0.12), 0.0, 1.45);
    air = clamp(max(uAudioB.w * 0.36, uTreble * 0.06), 0.0, 1.25);
  } else if (preset == 3.0) {
    // Void is intentionally near-empty: only subBass breathes the background and
    // air reveals rare distant dust. Other bands stay at detail/noise level.
    sub = clamp(max(uAudioA.x * 0.34, uBass * 0.16), 0.0, 1.35);
    body = clamp(max(uAudioA.y * 0.10, uBass * 0.08), 0.0, 1.35);
    lowMid = clamp(max(uAudioA.z * 0.10, uMid * 0.06), 0.0, 1.25);
    midFlow = clamp(max(uAudioA.w * 0.16, uMid * 0.08), 0.0, 1.25);
    high = clamp(max(uAudioB.x * 0.08, uTreble * 0.04), 0.0, 1.25);
    presence = clamp(max(uAudioB.y * 0.10, uTreble * 0.05), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.08, uAudioB.w * 0.06), 0.0, 1.35);
    kick = clamp(max(uAudioC.x * 0.06, uBeat * 0.04), 0.0, 1.45);
    air = clamp(max(uAudioB.w, uTreble * 0.18), 0.0, 1.25);
  } else if (preset == 4.0) {
    // Vinyl uses a narrow mapping: midFlow spins the record, low bands breathe
    // the disc, kick sweeps the rim, and presence/spark only catch scratches.
    sub = clamp(max(uAudioA.x, uBass * 0.18), 0.0, 1.35);
    body = clamp(max(uAudioA.y, uBass * 0.24), 0.0, 1.35);
    lowMid = clamp(max(uAudioA.z * 0.46, uMid * 0.08), 0.0, 1.25);
    midFlow = clamp(max(uAudioA.w, uMid * 0.18), 0.0, 1.25);
    high = clamp(max(uAudioB.x * 0.28, uTreble * 0.05), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.10), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.58, uAudioB.w * 0.16), 0.0, 1.35);
    kick = clamp(max(uAudioC.x * max(0.52, kickSoft) * max(0.70, kickRelease), uBeat * 0.14), 0.0, 1.45);
    air = clamp(max(uAudioB.w * 0.22, uTreble * 0.04), 0.0, 1.25);
  } else if (preset == 5.0) {
    // Galaxy should stay calm: a single main-beat envelope drives the outward
    // brightness breath, while all regular flow is slow time motion.
    sub = 0.0;
    body = 0.0;
    lowMid = 0.0;
    midFlow = 0.0;
    high = 0.0;
    presence = 0.0;
    spark = 0.0;
    kick = clamp(max(uAudioC.x * 0.42 * max(0.62, kickRelease), uBeat * 0.22), 0.0, 1.20);
    air = 0.0;
  } else if (preset == 7.0) {
    // Jellyfish is an independent model/procedural layer. Keep motion organic:
    // sub lifts the group, bassBody pulses the bells, lowMid/midFlow drive
    // tentacles and oral arms, and presence/air only light rims and plankton.
    sub = clamp(max(uAudioA.x, uBass * 0.34), 0.0, 1.35);
    body = clamp(max(uAudioA.y, uBass * 0.58), 0.0, 1.35);
    lowMid = clamp(max(uAudioA.z, uMid * 0.44), 0.0, 1.25);
    midFlow = clamp(max(uAudioA.w, uMid * 0.38), 0.0, 1.25);
    high = clamp(max(uAudioB.x * 0.22, uTreble * 0.14), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.28), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.46, uAudioB.w * 0.18), 0.0, 1.35);
    kick = clamp(max(uAudioC.x * 0.46 * max(0.60, kickSoft) * max(0.72, kickRelease), uBeat * 0.28), 0.0, 1.45);
    air = clamp(max(uAudioB.w, uTreble * 0.20), 0.0, 1.25);
  }
  sub = clamp(sub, 0.0, 1.35);
  body = clamp(body, 0.0, 1.35);
  lowMid = clamp(lowMid, 0.0, 1.25);
  midFlow = clamp(midFlow, 0.0, 1.25);
  high = clamp(high, 0.0, 1.25);
  presence = clamp(presence, 0.0, 1.25);
  spark = clamp(spark, 0.0, 1.35);
  kick = clamp(kick, 0.0, 1.45);
  air = clamp(air, 0.0, 1.25);
  float grooveDrive = saturate(body);
  float melodyDrive = saturate(midFlow);
  float accentDrive = saturate(kick);
  float detailDrive = saturate((high + presence + spark + air) * 0.16);
  float materialDrive = detailDrive;
  float macroStructure = saturate(uAudioD.x);
  float macroMotion = saturate(uAudioD.y);
  float macroPulse = saturate(uAudioD.z);
  float macroImpact = saturate(uAudioD.w);
  float K = intensity * 1.6;
  float t = uTime * speed;
  float seed = input.rnd * 6.2831853;
  float densityMask = 1.0;
  float3 themeColor = lerp(uPrimary.rgb, uSecondary.rgb, input.uv.y);
  float3 sampledCover = uCover.SampleLevel(uCoverSampler, saturate(input.uv), 0).rgb;
  sampledCover = pow(max(sampledCover, float3(0.0, 0.0, 0.0)), float3(0.92, 0.92, 0.92));
  float3 coverColor = lerp(themeColor, sampledCover, saturate(uUseTexture));
  if (preset == 0.0) {
    // Player preset 0 is authored on a 4.8 world-unit cover plane.
    // Keep that scale for motion/noise math, then map it back to desktop clip space.
    float2 silkBase = base * 2.4;
    float4 silkEdge = uEdge.SampleLevel(uCoverSampler, input.uv, 0.0);
    float depthVal = silkEdge.r;
    float edgeVal = silkEdge.g;
    float fgMask = silkEdge.b;
    float rippleZ = silkRippleSumAt(silkBase);
    float silkBassDrive = saturate(sub * 0.58 + body * 0.52);
    float silkWaveDrive = saturate(lowMid * 0.24 + midFlow * 0.18);
    float silkKickDrive = saturate(kick);
    float silkDetailDrive = saturate(high * 0.16 + presence * 0.28 + spark * 0.18 + air * 0.14);
    float silkWaveT = uTime * speed * (0.22 + midFlow * 0.10);
    float midN = snoise(float3(silkBase.x * 1.4, silkBase.y * 1.4, silkWaveT)) * 0.68
      + snoise(float3(silkBase.x * 2.8 + 5.0, silkBase.y * 2.8 - 3.0, silkWaveT * 1.42)) * 0.32;
    float midMask = 0.55 + 0.45 * snoise(float3(silkBase.x * 0.4, silkBase.y * 0.4, uTime * speed * 0.08));
    float midDisp = clamp(midN * silkWaveDrive * midMask * K, -0.085 * K, 0.085 * K);
    float trebleJ = snoise(float3(silkBase.x * 6.5, silkBase.y * 6.5, t * 3.5 + input.rnd * 4.0)) * silkDetailDrive * 0.22 * K;
    float centerMask = 1.0 - smoothstep(0.25, 3.05, length(silkBase));
    float centerFalloff = exp(-dot(silkBase, silkBase) / 5.6);
    float bassBreath = (snoise(float3(silkBase.x * 0.35, silkBase.y * 0.35, t * 0.4)) * silkBassDrive * 0.34
      + centerMask * sub * 0.24) * K;
    float horizontalWave = clamp(sin(silkBase.x * 1.48 - t * (0.42 + midFlow * 0.10)) * lowMid * 0.055 * K, -0.024 * K, 0.024 * K);
    float beatRing = exp(-abs(length(silkBase) - 1.12) * 3.4);
    float beatLift = (centerFalloff * 0.62 + beatRing * 0.38) * silkKickDrive * 0.135 * K;
    float contourGlow = edgeVal * presence * 0.24 * K;
    float depthZ = (depthVal - 0.5) * depth * 1.40 * saturate(uUseTexture);
    float2 silkPlane = silkBase;
    silkPlane *= 1.0 + centerFalloff * silkBassDrive * 0.014;
    float silkZ = rippleZ * 1.30 + midDisp + horizontalWave + trebleJ + bassBreath + beatLift + contourGlow + depthZ;
    if (uInteraction.z > 0.5) {
      float2 mouseWorld = uInteraction.xy * 2.4;
      float md = length(silkPlane - mouseWorld);
      if (md < 1.0) {
        float push = (1.0 - md) * (1.0 - md);
        silkZ += push * 0.55;
      }
    }
    if (uHand.z > 0.01) {
      float2 handWorld = uHand.xy * 2.4;
      float2 hdv = silkPlane - handWorld;
      float hd = length(hdv);
      float rad = 1.55;
      if (hd < rad) {
        float handPush = pow((rad - hd) / rad, 2.0) * saturate(uHand.z);
        silkZ += handPush * 1.10;
        silkPlane += hdv / max(0.001, hd) * handPush * 0.28;
      }
    }
    float silkGrip = saturate(uHand.w);
    if (silkGrip > 0.001) {
      float gripWave = 0.5 + 0.5 * sin(uTime * 2.2 + input.rnd * 6.2831);
      silkPlane *= lerp(1.0, 0.66 + gripWave * 0.035, silkGrip);
      silkZ += silkGrip * (0.18 + uBass * 0.22 + gripWave * 0.10);
    }
    silkZ = clamp(silkZ, -3.2, 3.6);
    // Player preset 0 is a cover grid displaced in Z. Do not apply scatter or
    // twist here; those additions turn the cloth mesh into a drifting point cloud.
    float3 silkWorld = float3(silkPlane.x, -silkPlane.y, silkZ * 0.76);
    ProjectedPoint silkProjected = projectWorld3(silkWorld, 6.6, 0.08, 0.0, 0.94);
    float zDepth = saturate((silkZ + 3.2) / 6.8);
    depthScale = saturate(0.50 + zDepth * 0.38 + fgMask * 0.18 + grooveDrive * 0.06 + accentDrive * 0.035);
    depthShade = 0.68 + depthScale * 0.34 + edgeVal * materialDrive * 0.10 + melodyDrive * 0.05 + accentDrive * 0.035;
    particleViewZ = silkProjected.viewZ;
    clipDepth = saturate(silkProjected.clipDepth + zDepth * 0.035);
    p = float2(silkProjected.p.x, silkProjected.p.y - 0.11);
    o.aux = max(o.aux, saturate(abs(silkZ) * 0.22 + rippleZ * 0.36 + abs(midDisp + horizontalWave) * 0.15 + beatLift * 0.30 + contourGlow * 0.20 + silkKickDrive * 0.06 + trebleJ * 0.16));
    o.color = float4(depthShade, edgeVal, fgMask, saturate(0.86 + depthScale * 0.18));
  } else if (preset == 1.0) {
    // Player preset 1 is a regular UV tube with cyber color/material treatment.
    // Keep motion in the original tube formula: flow, slow spin, radius ripple,
    // and local kick rings.
    float tunnelFlowDrive = saturate(midFlow);
    float tunnelPulseDrive = saturate(kick);
    float tunnelGrooveDrive = saturate(sub * 0.30 + body * 0.40 + lowMid * 0.28 + midFlow * 0.18);
    float spin = uRuntime.x;
    float flow = frac(input.uv.y - uRuntime.y);
    float zPos = (flow - 0.5) * 9.0;
    float angleTunnel = input.uv.x * 6.2831853 + spin;
    particleUv = float2(input.uv.x, flow);
    float tunnelNearDepth = smoothstep(-4.5, 4.5, zPos);
    float purpleBlueBlock = 1.0 - smoothstep(0.44, 0.58, tunnelNearDepth);
    float ringHead = frac(uAudioD.z);
    float ringDist = abs(frac(flow - ringHead + 0.5) - 0.5);
    float kickRing = exp(-pow(ringDist / 0.055, 2.0)) * tunnelPulseDrive * purpleBlueBlock;
    float breathPhase = frac(uRuntime.z);
    float deepBreath = 0.5 - 0.5 * cos(breathPhase * 6.2831853);
    float breathPulseTravel = frac(breathPhase + 0.50);
    float kickBeat = smoothstep(0.06, 0.78, tunnelPulseDrive);
    float grooveBeat = smoothstep(0.12, 0.76, tunnelGrooveDrive);
    float flowBeat = smoothstep(0.16, 0.82, saturate(lowMid * 0.32 + midFlow * 0.38 + macroMotion * 0.26 + presence * 0.08));
    float liftBeat = smoothstep(0.18, 0.72, saturate(macroMotion * 0.42 + macroStructure * 0.18 + lowMid * 0.20 + midFlow * 0.16));
    float ringBeat = saturate(max(kickBeat, max(grooveBeat * 0.86, flowBeat * 0.78)) + min(kickBeat, max(grooveBeat, flowBeat)) * 0.16 + liftBeat * 0.12);
    float outerAccent = saturate(uAudioC.y);
    float blueRingGate = smoothstep(0.24, 0.76, saturate(outerAccent * 0.42 + flowBeat * liftBeat * 0.26 + tunnelPulseDrive * 0.14 + macroImpact * 0.08));
    float heavyPulseGate = smoothstep(0.32, 0.86, saturate(outerAccent * 0.56 + tunnelPulseDrive * 0.46 + macroImpact * 0.34 + flowBeat * liftBeat * 0.18));
    float standingMask = exp(-pow((tunnelNearDepth - 0.52) / 0.090, 2.0));
    float standingSwell = exp(-pow((tunnelNearDepth - 0.52) / 0.135, 2.0)) * ringBeat * purpleBlueBlock;
    float standingRing = standingMask * (0.095 + ringBeat * 0.33) * purpleBlueBlock;
    float sourceBreath = saturate(0.080 + deepBreath * (0.22 + flowBeat * 0.055) + ringBeat * 0.20);
    float breathPulseActive = (1.0 - smoothstep(0.72, 1.0, breathPulseTravel)) * saturate(0.52 + sourceBreath + kickBeat * 0.14 + flowBeat * 0.10 + liftBeat * 0.08);
    float breathPulseHead = lerp(0.52, 0.96, breathPulseTravel);
    float latePulseTravel = smoothstep(0.50, 0.94, breathPulseTravel);
    float breathPulseRing = exp(-pow((tunnelNearDepth - breathPulseHead) / 0.060, 2.0)) * breathPulseActive * saturate(0.72 + sourceBreath * 0.26 + ringBeat * 0.16) * purpleBlueBlock;
    float eventTravel0 = tunnelPackedPulseTravel(uAudioB.z);
    float eventTravel1 = tunnelPackedPulseTravel(uRuntime.w);
    float eventStrength0 = tunnelPackedPulseStrength(uAudioB.z);
    float eventStrength1 = tunnelPackedPulseStrength(uRuntime.w);
    float strongEvent0 = smoothstep(0.94, 1.14, eventStrength0);
    float strongEvent1 = smoothstep(0.94, 1.14, eventStrength1);
    float eventRingRaw0 = tunnelEventPulseRing(uAudioB.z, tunnelNearDepth, 0.050);
    float eventRingRaw1 = tunnelEventPulseRing(uRuntime.w, tunnelNearDepth, 0.050);
    float eventSwellRaw0 = tunnelEventPulseRing(uAudioB.z, tunnelNearDepth, 0.088);
    float eventSwellRaw1 = tunnelEventPulseRing(uRuntime.w, tunnelNearDepth, 0.088);
    float eventPurpleLimit = 1.0 - smoothstep(0.76, 0.88, tunnelNearDepth);
    float eventPurpleGate0 = lerp(purpleBlueBlock, eventPurpleLimit, strongEvent0);
    float eventPurpleGate1 = lerp(purpleBlueBlock, eventPurpleLimit, strongEvent1);
    float eventPulseRing = saturate(eventRingRaw0 * eventPurpleGate0 + eventRingRaw1 * eventPurpleGate1);
    float eventBlueSwell = saturate(eventSwellRaw0 * smoothstep(0.36, 0.78, eventTravel0) * strongEvent0 + eventSwellRaw1 * smoothstep(0.36, 0.78, eventTravel1) * strongEvent1);
    float eventPulseSwell = saturate(eventSwellRaw0 * eventPurpleGate0 + eventSwellRaw1 * eventPurpleGate1) + eventBlueSwell * 0.42;
    float deepPurpleBreath = exp(-pow((tunnelNearDepth - 0.34) / 0.20, 2.0)) * (0.12 + deepBreath * 0.34 + ringBeat * 0.08) * purpleBlueBlock;
    float purpleRing = saturate(kickRing * 0.35 + standingRing * 0.96 + deepPurpleBreath * 0.72 + breathPulseRing * 0.36 + eventPulseRing * 0.34);
    float edgeDrive = smoothstep(0.08, 1.08, presence * 0.46 + spark * 0.34 + air * 0.20);
    float rail = pow(0.5 + 0.5 * cos(angleTunnel * 6.0), 7.0);
    float fineRail = pow(0.5 + 0.5 * sin(angleTunnel * 18.0 - zPos * 0.85 + t * (3.4 + air * 1.4)), 5.0);
    float railGlow = rail * (0.030 + edgeDrive * 0.105 + grooveBeat * 0.030);
    float baseR = 1.95 + sub * 0.18 - body * 0.10 * K;
    float ripG = sin(angleTunnel * 5.0 + zPos * 1.4 + t * 2.2) * 0.105 * (lowMid + high) * K;
    float r = baseR + ripG + kickRing * 0.18 + standingSwell * 0.075 + eventPulseSwell * 0.055;
    float3 tunnelWorld = float3(cos(angleTunnel) * r, sin(angleTunnel) * r, zPos);
    float cameraTheta = 0.0;
    float cameraPhi = 0.030;
    ProjectedPoint tunnelProjected = projectWorld3(tunnelWorld, 6.2, cameraPhi, cameraTheta, 1.0);
    float flowLine = min(flow + 0.004, 0.999);
    float zLine = (flowLine - 0.5) * 9.0;
    float ripGLine = sin(angleTunnel * 5.0 + zLine * 1.4 + t * 2.2) * 0.105 * (lowMid + high) * K;
    float rLine = baseR + ripGLine + kickRing * 0.18 + standingSwell * 0.075 + eventPulseSwell * 0.055;
    float3 tunnelLineWorld = float3(cos(angleTunnel) * rLine, sin(angleTunnel) * rLine, zLine);
    ProjectedPoint tunnelLineProjected = projectWorld3(tunnelLineWorld, 6.2, cameraPhi, cameraTheta, 1.0);
    float screenSpin = 0.0;
    float ss = sin(screenSpin);
    float cs2 = cos(screenSpin);
    float2 tunnelP = float2(tunnelProjected.p.x * cs2 - tunnelProjected.p.y * ss, tunnelProjected.p.x * ss + tunnelProjected.p.y * cs2);
    float2 tunnelLineP = float2(tunnelLineProjected.p.x * cs2 - tunnelLineProjected.p.y * ss, tunnelLineProjected.p.x * ss + tunnelLineProjected.p.y * cs2);
    float screenAspect = max(0.35, uWidth / max(uHeight, 1.0));
    float wallProjectedRadius = length(float2(tunnelP.x * screenAspect, tunnelP.y));
    float wallDepthMask = smoothstep(0.00, 0.08, tunnelNearDepth) * (1.0 - smoothstep(0.44, 0.58, tunnelNearDepth));
    float wallProjectedBand = smoothstep(0.42, 0.58, wallProjectedRadius) * (1.0 - smoothstep(1.03, 1.20, wallProjectedRadius));
    float wallFacingMask = 0.74 + 0.26 * pow(saturate(abs(sin(angleTunnel))), 0.58);
    float fullPulseVs = eventPulseRing * max(step(1.28, eventStrength0), step(1.28, eventStrength1));
    float purpleRegionMaskVs = saturate(wallDepthMask * wallProjectedBand * wallFacingMask * purpleBlueBlock);
    float purpleRegionLiftVs = purpleRegionMaskVs * saturate(0.10 + ringBeat * 0.18 + edgeDrive * 0.12 + presence * 0.05);
    structureLineDir = normalize(tunnelLineP - tunnelP + float2(0.0001, 0.0001));
    structureLineWeight = 1.00;
    depthScale = saturate(tunnelNearDepth * 0.92 + tunnelProjected.depthWeight * 0.08);
    depthShade = 0.36 + tunnelNearDepth * 0.72 + purpleRegionLiftVs * 0.22 + fullPulseVs * 0.14;
    particleViewZ = tunnelProjected.viewZ;
    clipDepth = tunnelProjected.clipDepth;
    p = tunnelP;
    o.beat = purpleRegionMaskVs;
    o.aux = max(o.aux, saturate(fullPulseVs * 0.32));
    o.color = float4(depthShade, purpleRing, tunnelNearDepth, saturate(depthShade));
  } else if (preset == 2.0) {
    float theta = input.uv.x * 6.2831853;
    float phi = (input.uv.y - 0.5) * 3.1415926;
    float shellOpen = saturate(uAudioA.x);
    float breathCompress = saturate(uAudioA.y);
    float detailGate = saturate(uAudioA.z);
    float flowDrive = saturate(uAudioA.w);
    float waveDrive = saturate(detailGate);
    float detailHead = frac(uRuntime.z + flowDrive * 0.025);
    float fromTop = 1.0 - input.uv.y;
    float detailFade = smoothstep(0.02, 0.12, detailHead) * (1.0 - smoothstep(0.86, 0.98, detailHead));
    float waveWidth = 0.040 + waveDrive * 0.012 + flowDrive * 0.006;
    float downWave = exp(-pow((fromTop - detailHead) / waveWidth, 2.0)) * detailFade;
    float eventTravel0 = orbitPackedWaveTravel(uAudioB.z);
    float eventTravel1 = orbitPackedWaveTravel(uAudioB.w);
    float eventTravel2 = orbitPackedWaveTravel(uAudioC.y);
    float eventStrength0 = orbitPackedWaveStrength(uAudioB.z);
    float eventStrength1 = orbitPackedWaveStrength(uAudioB.w);
    float eventStrength2 = orbitPackedWaveStrength(uAudioC.y);
    float eventColor0 = orbitPackedWaveColor(uAudioB.z);
    float eventColor1 = orbitPackedWaveColor(uAudioB.w);
    float eventColor2 = orbitPackedWaveColor(uAudioC.y);
    float eventBoost0 = orbitPackedWaveBoost(uAudioB.z);
    float eventBoost1 = orbitPackedWaveBoost(uAudioB.w);
    float eventBoost2 = orbitPackedWaveBoost(uAudioC.y);
    float eventLife0 = orbitPackedWaveLife(eventTravel0) * step(0.01, eventStrength0);
    float eventLife1 = orbitPackedWaveLife(eventTravel1) * step(0.01, eventStrength1);
    float eventLife2 = orbitPackedWaveLife(eventTravel2) * step(0.01, eventStrength2);
    float leadWidth = 0.0068;
    float bodyWidth = 0.025;
    float lead0 = exp(-pow((fromTop - eventTravel0) / leadWidth, 2.0)) * eventLife0 * eventStrength0;
    float lead1 = exp(-pow((fromTop - eventTravel1) / leadWidth, 2.0)) * eventLife1 * eventStrength1;
    float lead2 = exp(-pow((fromTop - eventTravel2) / leadWidth, 2.0)) * eventLife2 * eventStrength2;
    float leadBest = lead0;
    float leadColorId = eventColor0;
    float leadEventBoost = eventBoost0;
    if (lead1 > leadBest) {
      leadBest = lead1;
      leadColorId = eventColor1;
      leadEventBoost = eventBoost1;
    }
    if (lead2 > leadBest) {
      leadBest = lead2;
      leadColorId = eventColor2;
      leadEventBoost = eventBoost2;
    }
    float bodyHead0 = eventTravel0 - 0.038;
    float bodyHead1 = eventTravel1 - 0.038;
    float bodyHead2 = eventTravel2 - 0.038;
    float wave0 = exp(-pow((fromTop - bodyHead0) / bodyWidth, 2.0)) * eventLife0 * eventStrength0;
    float wave1 = exp(-pow((fromTop - bodyHead1) / bodyWidth, 2.0)) * eventLife1 * eventStrength1;
    float wave2 = exp(-pow((fromTop - bodyHead2) / bodyWidth, 2.0)) * eventLife2 * eventStrength2;
    float leadGlow = saturate(leadBest * (1.18 + leadEventBoost * 0.22));
    float poolWave = saturate(max(max(wave0, wave1), wave2));
    float detailWave = detailGate * downWave;
    float equator = exp(-pow(phi / 0.20, 2.0));
    float shockHead = frac(detailHead + 0.18 + flowDrive * 0.040);
    float shockDist = abs(frac(input.uv.x - shockHead + 0.5) - 0.5);
    float shock = exp(-pow(shockDist / 0.052, 2.0)) * equator * saturate(uAudioC.x);
    float orbitRail = pow(0.5 + 0.5 * cos(theta * 6.0 + phi * 0.72 + t * (0.34 + flowDrive * 0.26)), 7.0) * detailWave;
    float fineRail = pow(0.5 + 0.5 * sin(theta * 18.0 - phi * 5.2 + t * (1.9 + flowDrive * 0.34)), 5.0) * detailWave;
    float latitudeMask = 0.38 + 0.62 * pow(abs(cos(phi)), 0.65);
    float latitudeFlow = sin(phi * 9.2 + theta * (0.36 + flowDrive * 0.16) + t * (0.42 + flowDrive * 0.38))
      * (0.006 + detailWave * 0.018 + flowDrive * 0.006) * latitudeMask * detailWave;
    float railLift = (orbitRail * 0.010 + fineRail * 0.004) * latitudeMask;
    float shockWave = shock * (0.010 + flowDrive * 0.010);
    float flowSwell = flowDrive * detailGate * 0.006;
    float pairIndex = floor(input.uv.x * 12.0);
    float pairAlt = step(0.5, frac(pairIndex * 0.5));
    float pairLocal = frac(input.uv.x * 12.0);
    float pairRidge = pow(saturate(1.0 - abs(pairLocal - 0.5) * 2.0), 0.55);
    float pairShapeA = 0.72 + 0.42 * sin(phi * 3.6 + pairIndex * 0.71 + t * 0.18);
    float pairShapeB = 0.74 + 0.46 * cos(phi * 5.2 - pairIndex * 0.43 + t * 0.13);
    float meridianPairShape = 0.72 + pairRidge * saturate(lerp(pairShapeA, pairShapeB, pairAlt)) * 0.72;
    float poolRadiusWave = poolWave * 0.078 * meridianPairShape;
    float radiusDetail = clamp((latitudeFlow + railLift + shockWave + flowSwell) * 1.34 + poolRadiusWave, -0.044, 0.118);
    orbitPointBoost = saturate(detailWave * 0.30 + leadGlow * 0.34 + shock * 0.22 + flowDrive * 0.06 + abs(radiusDetail) * 2.2);
    float baseExpand = shellOpen * 0.22;
    float r = 2.38 * (1.0 + baseExpand - breathCompress * 0.083 + radiusDetail);
    float3 sPos = float3(r * cos(phi) * cos(theta), r * sin(phi), r * cos(phi) * sin(theta));
    float yaw = uRuntime.x + flowDrive * 0.035;
    float cy = cos(yaw), sy = sin(yaw);
    sPos.xz = float2(sPos.x * cy - sPos.z * sy, sPos.x * sy + sPos.z * cy);
    ProjectedPoint orbitProjected = projectWorld3(sPos, 7.0, 0.15, 0.0, 0.84);
    float thetaLine = theta + 0.018;
    float rTheta = r;
    float3 orbitThetaPos = float3(rTheta * cos(phi) * cos(thetaLine), rTheta * sin(phi), rTheta * cos(phi) * sin(thetaLine));
    orbitThetaPos.xz = float2(orbitThetaPos.x * cy - orbitThetaPos.z * sy, orbitThetaPos.x * sy + orbitThetaPos.z * cy);
    ProjectedPoint orbitThetaProjected = projectWorld3(orbitThetaPos, 7.0, 0.15, 0.0, 0.84);
    float2 thetaDir = normalize(orbitThetaProjected.p - orbitProjected.p + float2(0.0001, 0.0001));
    float phiLine = clamp(phi + 0.018, -1.5600, 1.5600);
    float rPhi = r;
    float3 orbitPhiPos = float3(rPhi * cos(phiLine) * cos(theta), rPhi * sin(phiLine), rPhi * cos(phiLine) * sin(theta));
    orbitPhiPos.xz = float2(orbitPhiPos.x * cy - orbitPhiPos.z * sy, orbitPhiPos.x * sy + orbitPhiPos.z * cy);
    ProjectedPoint orbitPhiProjected = projectWorld3(orbitPhiPos, 7.0, 0.15, 0.0, 0.84);
    float2 phiDir = normalize(orbitPhiProjected.p - orbitProjected.p + float2(0.0001, 0.0001));
    float lonFineWeight = pow(saturate(1.0 - abs(frac(input.uv.x * 24.0) - 0.5) * 2.0), 7.0) * detailWave;
    float latFineWeight = pow(saturate(1.0 - abs(frac(input.uv.y * 12.0) - 0.5) * 2.0), 7.0) * detailWave;
    float lonMajorWeight = pow(saturate(1.0 - abs(frac(input.uv.x * 8.0) - 0.5) * 2.0), 5.0) * detailWave;
    float latMajorWeight = pow(saturate(1.0 - abs(frac(input.uv.y * 6.0) - 0.5) * 2.0), 5.0) * detailWave;
    float lonWeight = max(lonFineWeight, lonMajorWeight);
    float latWeight = max(max(latFineWeight, latMajorWeight), equator * 0.20 * detailWave);
    float usePhiDir = saturate((lonWeight - latWeight) * 0.5 + 0.5);
    structureLineDir = normalize(lerp(thetaDir, phiDir, usePhiDir) + float2(0.0001, 0.0001));
    structureLineWeight = 1.00;
    float orbitNear = saturate(sPos.z / max(0.8, r) * 0.5 + 0.5);
    depthScale = saturate(orbitNear * 0.76 + orbitProjected.depthWeight * 0.24);
    depthShade = 0.28 + orbitNear * 0.48 + orbitProjected.depthWeight * 0.16 + detailWave * 0.08 + leadGlow * 0.32 + leadEventBoost * leadGlow * 0.12 + shock * 0.18;
    particleViewZ = orbitProjected.viewZ;
    clipDepth = orbitProjected.clipDepth;
    p = orbitProjected.p;
    o.aux = max(o.aux, saturate(detailWave * 0.14 + leadGlow * 0.74 + leadEventBoost * leadGlow * 0.18 + shock * 0.34));
    o.beat = leadColorId + leadEventBoost * 0.99;
    o.color = float4(depthShade, saturate(detailWave), saturate(shock + leadGlow * 0.96 + leadEventBoost * leadGlow * 0.12), saturate(0.42 + shellOpen * 0.58));
  } else if (preset == 3.0) {
    float dustSeed = hash(input.rnd * 991.0 + floor(input.uv.y * 160.0));
    float dust = smoothstep(0.958, 0.999, dustSeed);
    float drift = frac(input.uv.x + t * (0.0008 + dustSeed * 0.0022 + melodyDrive * 0.0028));
    float voidCore = exp(-pow((input.uv.y - 0.5) / 0.24, 2.0)) * smoothstep(0.06, 0.76, grooveDrive + melodyDrive * 0.62);
    float rippleAmp = (dust * 0.65 + voidCore * 0.35) * (melodyDrive * 0.030 + grooveDrive * 0.018);
    float3 voidWorld = float3(
      (drift - 0.5) * 24.0,
      (input.uv.y - 0.5) * (13.0 + grooveDrive * 2.2) + sin(t * (0.05 + melodyDrive * 0.03) + dustSeed * 6.0) * (0.18 + melodyDrive * 0.42),
      lerp(-30.0, -8.0, dustSeed) + voidCore * (3.8 + grooveDrive * 2.0)
    );
    ProjectedPoint voidProjected = projectWorld3(voidWorld, 8.0, 0.05, 0.0, 1.0);
    depthScale = saturate(voidProjected.depthWeight * 0.74 + dustSeed * 0.18);
    depthShade = 0.42 + melodyDrive * 0.18 + grooveDrive * 0.10 + dustSeed * 0.10;
    particleViewZ = voidProjected.viewZ;
    clipDepth = voidProjected.clipDepth;
    p = voidProjected.p;
    float voidAlpha = (dust * 0.72 + voidCore * 0.28) * (0.26 + melodyDrive * 0.20 + grooveDrive * 0.10);
    float3 voidColor = lerp(float3(0.30, 0.44, 0.62), float3(0.76, 0.98, 1.0), dustSeed) * (0.92 + melodyDrive * 0.28 + materialDrive * 0.10);
    o.aux = rippleAmp;
    o.color = float4(voidColor * depthShade, saturate(voidAlpha));
  } else if (preset == 4.0) {
    float bassDrive = smoothstep(0.08, 0.88, grooveDrive * 1.34);
    float highDrive = smoothstep(0.05, 0.72, materialDrive * 0.74);
    float hiResGuard = smoothstep(1.08, 1.55, max(0.1, uRuntime.w));
    float edgeGuard = lerp(1.0, 0.38, hiResGuard);
    float depthGuard = lerp(1.0, 0.44, hiResGuard);
    float grooveGuard = lerp(1.0, 0.48, hiResGuard);
    float beatGuard = lerp(1.0, 0.36, hiResGuard);
    float2 recordPlane = (input.uv - 0.5) * 5.12;
    float angle0 = atan2(recordPlane.y, recordPlane.x);
    float recordR = 2.46;
    float coverR = 1.18;
    float spin = uRuntime.x;
    float cs = cos(spin), sn = sin(spin);
    float2 rp = float2(recordPlane.x * cs - recordPlane.y * sn, recordPlane.x * sn + recordPlane.y * cs);
    float d = length(recordPlane);
    float coverMask = 1.0 - smoothstep(coverR - 0.012, coverR + 0.018, d);
    float recordAlpha = 1.0 - smoothstep(recordR - 0.02, recordR + 0.05, d);
    float border = exp(-pow((d - coverR) / 0.064, 2.0)) * edgeGuard;
    float outerRim = exp(-pow((d - (recordR - 0.050)) / 0.055, 2.0)) * edgeGuard;
    float vinylN = saturate((d - coverR) / max(0.001, recordR - coverR));
    float groove = 0.5 + 0.5 * sin((d - coverR) * lerp(98.0, 58.0, hiResGuard));
    float fineGroove = 0.5 + 0.5 * sin((d - coverR) * lerp(170.0, 92.0, hiResGuard) + input.rnd * 3.0);
    float tick = smoothstep(0.82, 0.995, hash(floor((angle0 + 3.1415926) * 38.0) + floor(d * 72.0) * 2.1));
    float sweepHead = uTime * (1.08 + melodyDrive * 0.34);
    float sweep = pow(0.5 + 0.5 * cos(angle0 - sweepHead), 18.0) * accentDrive;
    float discN = saturate(d / recordR);
    float radialRipple = max(0.0, sin(d * 7.4 - t * (2.15 + melodyDrive * 0.72) + accentDrive * 1.7));
    radialRipple *= recordAlpha * (0.026 + melodyDrive * 0.075 + accentDrive * 0.048) * K;
    float pillarRing = floor(discN * 24.0);
    float pillarArc = floor((angle0 + 3.1415926) / 6.2831852 * (44.0 + pillarRing * 0.32));
    float pillarSeed = hash(pillarRing * 37.17 + pillarArc * 11.13);
    float pillarCell = smoothstep(0.26, 0.96, pillarSeed);
    float pillarDrive = smoothstep(0.06, 0.86, grooveDrive * 0.56 + melodyDrive * 0.42 + accentDrive * 0.52);
    float pillarLane = recordAlpha * (1.0 - smoothstep(recordR - 0.24, recordR + 0.01, d)) * lerp(0.42, 1.0, 1.0 - coverMask);
    float pillarHeight = pillarCell * pillarDrive * pillarLane * (0.080 + vinylN * 0.260 + border * 0.055) * K * lerp(1.0, 0.72, hiResGuard);
    float meteorSeed = hash(floor(input.uv.y * 84.0) * 17.7 + floor(input.uv.x * 13.0) * 3.1);
    float meteorLane = smoothstep(0.948, 0.998, meteorSeed) * recordAlpha;
    float meteorPhase = frac(input.uv.x + t * (0.24 + spark * 0.16 + materialDrive * 0.050) + meteorSeed * 0.71);
    float meteorCore = exp(-pow((meteorPhase - 0.58) / 0.105, 2.0));
    float2 meteorDir = normalize(float2(0.88, -0.48));
    float2 meteorPerp = float2(-meteorDir.y, meteorDir.x);
    float2 meteorPlane = meteorDir * ((meteorPhase - 0.5) * 5.6) + meteorPerp * ((meteorSeed - 0.5) * 3.7);
    float meteorInside = 1.0 - smoothstep(recordR * 0.92, recordR * 1.12, length(meteorPlane));
    float meteorGlow = saturate(meteorLane * meteorCore * meteorInside * smoothstep(0.05, 0.78, materialDrive + spark * 0.48) * 1.25);
    float zDisc = coverMask * (0.040 + border * 0.030 * depthGuard + grooveDrive * 0.020 * beatGuard + sweep * border * 0.046)
      + (1.0 - coverMask) * (groove * 0.012 * grooveGuard + border * 0.030 * depthGuard + bassDrive * vinylN * 0.022 * K * beatGuard + sweep * outerRim * 0.042)
      + pillarHeight * lerp(0.56, 1.0, 1.0 - coverMask)
      + radialRipple * lerp(0.68, 1.0, 1.0 - coverMask)
      + meteorGlow * 0.72;
    float recordBreath = 1.0 + bassDrive * 0.020 * beatGuard + grooveDrive * 0.014 * beatGuard;
    float aspect = max(0.35, uWidth / max(1.0, uHeight));
    p = float2(rp.x / aspect, rp.y) * 0.42 * recordBreath;
    float topographyLift = saturate(pillarHeight * 3.4 + radialRipple * 1.25 + sweep * outerRim * 0.72 + meteorGlow * 0.50) * recordAlpha;
    p += float2(0.034 / aspect, -0.118) * topographyLift * depth;
    p = lerp(p, float2(meteorPlane.x / aspect, meteorPlane.y) * 0.42, saturate(meteorGlow));
    depthScale = saturate(0.42 + coverMask * 0.18 + border * 0.20 + outerRim * 0.22 + zDisc * depth * 0.75 + pillarHeight * 0.58 + meteorGlow * 0.28);
    depthShade = 0.48 + depthScale * 0.34 + groove * 0.10 + fineGroove * 0.04 + sweep * outerRim * 0.58 + melodyDrive * 0.04 + pillarHeight * 0.58 + radialRipple * 0.46 + meteorGlow * 0.76;
    particleViewZ = -7.2 + zDisc * depth * 0.34;
    clipDepth = saturate((7.2 - 1.2) / 52.0 + zDisc * depth * 0.012);
    float whiteRing = max(border * 0.92, outerRim * 0.26);
    o.aux = saturate(border * 0.70 + outerRim * 0.50 + tick * highDrive * 0.42 * grooveGuard + sweep * outerRim * 0.82 + pillarHeight * 2.3 + radialRipple * 1.45 + meteorGlow);
    o.color = float4(depthShade, coverMask, whiteRing, saturate(max(recordAlpha * (0.70 + coverMask * 0.22 + whiteRing * 0.28), meteorGlow * 0.92)));
  } else if (preset == 5.0) {
    float lane = input.uv.y;
    float beatPulse = saturate(max(macroPulse, accentDrive * 0.35));
    float twinkle = 0.42 + 0.58 * pow(0.5 + 0.5 * sin(t * (0.16 + input.rnd * 0.20) + input.rnd * 17.0), 2.0);
    float ribbonAlpha = 0.0;
    float glowOut = 0.0;
    float beatGlowOut = 0.0;
    float zLayer = 0.0;
    float3 galaxyColor = lerp(float3(0.32, 0.78, 1.0), float3(0.82, 0.42, 1.0), input.uv.y);
    if (lane < 0.80) {
      float laneWarp = snoise(float3(input.uv.x * 0.42, lane * 1.7, t * 0.018)) * 0.095
        + (hash(input.rnd * 73.1) - 0.5) * 0.045;
      float warpedLane = clamp(lane + laneWarp, 0.0, 0.80);
      float bandCoord = warpedLane / 0.80 * 5.65 + snoise(float3(input.uv.x * 0.82, lane * 2.25, t * 0.032)) * 0.62;
      float band = floor(bandCoord);
      float local = frac(bandCoord + hash(band * 9.13 + input.rnd * 2.4) * 0.18);
      float bandN = clamp((band + 0.5) / 5.65, 0.0, 1.0);
      float seed5 = hash(band * 19.17 + input.rnd * 31.0);
      float flow = frac(input.uv.x + t * (0.0018 + bandN * 0.0022 + seed5 * 0.0012) + seed5 * 0.53);
      float arc = (flow - 0.5) * 3.1415926 * (1.35 + bandN * 0.72 + seed5 * 0.24);
      float armCurve = sin(arc + bandN * 2.2 + seed5 * 5.3);
      float spiralRadius = 10.4 + bandN * 13.2 + seed5 * 6.8 + local * 3.2;
      float worldX = cos(arc * 0.72 + bandN * 0.92 + seed5 * 1.3) * spiralRadius + (flow - 0.5) * (15.2 + bandN * 10.8);
      float ribbonPhase = flow * 3.1415926 * 2.0 * (0.55 + bandN * 0.24 + seed5 * 0.10) + t * (0.010 + bandN * 0.007) + seed5 * 5.7;
      float broadWave = sin(ribbonPhase) * 0.78;
      float fineWave = sin(ribbonPhase * (1.36 + seed5 * 0.62) - t * 0.044 + seed5 * 5.0) * 0.045;
      float worldY = (bandN - 0.5) * 15.0 + armCurve * (2.6 + bandN * 1.8) + (seed5 - 0.5) * 2.10
        + snoise(float3(bandN * 2.0, flow * 0.62, seed5)) * 1.04
        + broadWave + fineWave + (local - 0.5) * 0.66;
      float ridgeCenter = 0.43 + (seed5 - 0.5) * 0.18;
      float ridge = exp(-pow((local - ridgeCenter) / (0.25 + seed5 * 0.04), 2.0));
      float ridgeFocus = pow(saturate(ridge), 0.75);
      float ridgeFloor = lerp(0.24, 0.10, bgBright5);
      float laneFocus = lerp(ridgeFloor, 1.14, ridgeFocus) * lerp(1.0, 0.86, bgDark5 * (1.0 - ridgeFocus));
      float softMask = smoothstep(0.010, 0.12, lane) * (1.0 - smoothstep(0.72, 0.81, lane));
      float ribbonNoise = snoise(float3(flow * 1.18 + seed5, bandN * 2.0, t * 0.018)) * 0.74;
      zLayer = lerp(-23.5, 15.5, bandN) + (seed5 - 0.5) * 6.0 + broadWave * 1.34 + ribbonNoise * 1.85;
      worldX += ribbonNoise * 1.40 + sin(t * 0.012 + seed5 * 8.0) * 0.22;
      ProjectedPoint galaxyProjected = projectWorld3(float3(worldX, worldY, zLayer) * 0.36, 9.4, 0.34, -0.52, 0.92);
      structureLineWeight = 0.0;
      float galaxyNear = saturate(zLayer / 24.0 + 0.5);
      depthScale = saturate(galaxyNear * 0.64 + galaxyProjected.depthWeight * 0.36);
      depthShade = 0.46 + depthScale * 0.42 + ridge * 0.18;
      particleViewZ = galaxyProjected.viewZ;
      clipDepth = galaxyProjected.clipDepth;
      p = galaxyProjected.p + float2(0.0, -0.18);
      float spineCoord = saturate(flow * 0.74 + bandN * 0.20 + local * 0.035);
      float spineOrigin = saturate(0.46 + (bandN - 0.5) * 0.08 + (seed5 - 0.5) * 0.045);
      float spineDist = abs(spineCoord - spineOrigin);
      float beatGlow = galaxyBeatWaveSum(spineDist);
      float beatMain = beatGlow * lerp(0.18, 1.0, ridgeFocus) * lerp(1.0, 0.62, bgBright5);
      float3 aurora = lerp(float3(0.52, 0.86, 1.0), float3(0.70, 0.58, 1.0), bandN);
      float3 beatTint = lerp(float3(0.36, 0.82, 1.16), float3(0.92, 0.44, 1.28), saturate(bandN * 0.74 + local * 0.18));
      aurora = lerp(aurora, beatTint, beatMain * 0.35);
      float ridgeAir = pow(saturate(ridge), 1.28);
      ribbonAlpha = (0.060 + ridgeAir * 0.86 + beatMain * 0.105)
        * softMask * 0.84 * laneFocus;
      galaxyColor = lerp(coverColor, aurora, 0.66 + ridge * 0.22) * (0.90 + ridge * 0.88) + beatTint * beatMain * 0.22;
      glowOut = ridge * 0.082 + beatMain * 0.15;
      beatGlowOut = max(beatGlowOut, beatMain);
      galaxySizeFocus = ridgeFocus * (1.0 - bgBright5 * 0.30);
    } else {
      float q = (lane - 0.80) / 0.20;
      float seed5 = hash(input.rnd * 917.0 + floor(q * 130.0));
      zLayer = lerp(-32.0, 18.0, seed5) + sin(t * (0.020 + seed5 * 0.032) + input.rnd * 8.0) * 1.05;
      float drift = frac(input.uv.x + t * (0.0012 + seed5 * 0.0040) + seed5 * 0.63);
      float cluster = snoise(float3(seed5 * 2.0, q * 3.2, t * 0.007)) * 3.4;
      float worldX = (drift - 0.5) * (54.0 + seed5 * 28.0) + cluster * 1.12;
      float worldY = (hash(input.rnd * 331.0 + seed5 * 5.0) - 0.5) * 27.0 + sin(t * (0.018 + seed5 * 0.028) + seed5 * 7.0) * 0.96;
      ProjectedPoint galaxyDustProjected = projectWorld3(float3(worldX, worldY * 0.82, zLayer) * 0.36, 9.4, 0.34, -0.52, 0.82);
      float dustNear = saturate(zLayer / 26.0 + 0.5);
      depthScale = saturate(dustNear * 0.62 + galaxyDustProjected.depthWeight * 0.32);
      depthShade = 0.40 + depthScale * 0.48 + twinkle * 0.22;
      particleViewZ = galaxyDustProjected.viewZ;
      clipDepth = galaxyDustProjected.clipDepth;
      p = galaxyDustProjected.p + float2(0.0, -0.18);
      float dustCoord = saturate(drift * 0.72 + q * 0.20 + seed5 * 0.08);
      float dustOrigin = saturate(0.44 + (q - 0.5) * 0.05 + (seed5 - 0.5) * 0.06);
      float beatGlow = galaxyBeatWaveSum(abs(dustCoord - dustOrigin));
      float beatDust = beatGlow * lerp(0.12, 0.06, bgBright5);
      float dustSeed2 = hash(input.rnd * 661.0 + floor(q * 160.0));
      float dust = pow(smoothstep(0.70, 0.997, dustSeed2), 1.90);
      float dustKeep = lerp(0.38, 0.20, bgBright5);
      dustKeep = lerp(dustKeep, 0.44, bgDark5);
      dust *= dustKeep;
      float desktopBright = bgBright5;
      float desktopBusy = bgConfidence5 * smoothstep(0.24, 0.66, max(saturate(uBackground.y), saturate(uBackground.z)));
      float dustDesktopGain = lerp(1.0, 0.58, saturate(max(desktopBright, desktopBusy * 0.72)));
      ribbonAlpha = dust * (0.014 + twinkle * 0.030 + beatDust * 0.014) * (1.0 - q * 0.22) * dustDesktopGain * lerp(1.0, 0.42, bgBright5);
      float3 dustBeatTint = lerp(float3(0.48, 0.74, 1.14), float3(0.86, 0.46, 1.22), saturate(q + seed5 * 0.24));
      galaxyColor = lerp(coverColor, dustBeatTint, 0.54 + twinkle * 0.065) * (0.60 + twinkle * 0.14) + dustBeatTint * beatDust * 0.050;
      glowOut = beatDust * 0.040;
      beatGlowOut = max(beatGlowOut, beatDust);
    }
    if (preset != 5.0) {
      p += float2(sin(uTime * 0.012 + seed), cos(uTime * 0.010 + seed * 1.7)) * (0.012 + midFlow * 0.010);
    }
    o.aux = saturate(glowOut);
    o.beat = saturate(beatGlowOut);
    o.color = float4(galaxyColor, saturate(ribbonAlpha));
  } else if (preset == 6.0) {
    float3 m = input.model;
    float kind = input.part;
    float jawGroup = step(1.0, kind);
    float boneKind = frac(kind);
    float toothBand = smoothstep(0.48, 0.70, m.z) * (1.0 - smoothstep(0.27, 0.48, abs(m.x))) * (1.0 - smoothstep(0.18, 0.46, abs(m.y + 0.72)));
    float toothNoise = hash(input.rnd * 21731.0 + floor((m.x + 0.52) * 21.0) * 5.137);
    m.y += toothBand * (toothNoise - 0.5) * 0.020;
    m.z += toothBand * (hash(input.rnd * 17923.0 + m.y * 31.0) - 0.5) * 0.012;
    float skullKick = saturate(kick + uBeat * 0.72 + body * 0.28);
    float jawOpen = saturate(0.60 + sin(uTime * 0.50) * 0.050 + body * 0.055 + skullKick * 0.075);
    float jawSideAnchor = smoothstep(0.36, 0.66, abs(m.x)) * (1.0 - smoothstep(0.78, 0.98, abs(m.x))) * smoothstep(-0.34, -0.74, m.y) * (1.0 - smoothstep(0.62, 0.86, m.z));
    float jawMotion = jawGroup * (1.0 - jawSideAnchor * 0.32);
    float jawDrop = jawMotion * smoothstep(-0.32, -0.88, m.y) * (0.58 + smoothstep(0.18, 0.62, abs(m.x)) * 0.04);
    float jawAngle = jawOpen * 0.52 * jawMotion;
    float jc0 = cos(jawAngle), js0 = sin(jawAngle);
    float2 hinge = float2(-0.45, 0.18);
    float2 jr = m.yz - hinge;
    float2 openedJaw = float2(jr.x * jc0 - jr.y * js0, jr.x * js0 + jr.y * jc0) + hinge;
    m.yz = lerp(m.yz, openedJaw, jawMotion);
    m.y -= jawDrop * (0.038 + jawOpen * 0.100);
    m.z += jawDrop * (0.003 + jawOpen * 0.014);
    float breath = sin(uTime * 0.33 + 1.7) * 0.018 + sin(uTime * 0.61 + 0.4) * 0.006;
    float flash = saturate(pow(max(0.0, skullKick - 0.12) / 0.88, 1.22) * 0.74 + max(0.0, high - 0.34) * 0.24 + max(0.0, body - 0.62) * 0.12);
    float amp = saturate((body + lowMid) * 0.010 + flash * 0.045);
    float rx = -0.26 + sin(uTime * 0.24 + 2.6) * 0.010 + cinemaShake * 0.010;
    float cx = cos(rx), sx = sin(rx);
    m.yz = float2(m.y * cx - m.z * sx, m.y * sx + m.z * cx);
    float scaleSkull = 0.62 * (1.0 + amp);
    p = float2(m.x * scaleSkull + breath, m.y * scaleSkull + 0.15 + sin(uTime * 0.38 + 0.2) * 0.012);
    p.x *= 0.92;
    p.y *= 1.02;
    float3 nrm = normalize(float3(input.model.x * 0.82, input.model.y * 0.68, input.model.z * 1.22 + 0.16));
    float key = pow(max(dot(nrm, normalize(float3(-0.48, 0.64, 0.60))), 0.0), 1.18);
    float low = pow(max(dot(nrm, normalize(float3(-0.10, -0.78, 0.34))), 0.0), 1.34) * 0.10;
    float fill = max(dot(nrm, normalize(float3(0.36, -0.04, 0.64))), 0.0) * 0.055;
    float rimLight = pow(max(dot(nrm, normalize(float3(0.88, 0.18, -0.44))), 0.0), 2.50) * (0.24 + max(0.0, uVisualB.w) * 0.08 + flash * 0.62);
    float gothicShadow = smoothstep(-0.10, 0.36, dot(nrm, normalize(float3(0.44, -0.06, -0.58))));
    float dentalLift = toothBand * (0.62 + toothNoise * 0.20);
    float lit = saturate(0.115 + key * 1.02 + low + fill + dentalLift * 0.20 + boneKind * 0.070 + flash * 0.56 - gothicShadow * 0.08);
    float3 boneA = lerp(float3(0.72, 0.68, 0.58), uPrimary.rgb, 0.22);
    float3 boneB = lerp(float3(1.00, 0.96, 0.84), uHighlight.rgb, 0.24);
    float3 shadow = lerp(float3(0.06, 0.05, 0.05), uSecondary.rgb, 0.08);
    float3 light = lerp(float3(1.00, 0.89, 0.62), uHighlight.rgb, 0.46);
    float3 bone = lerp(boneA, boneB, saturate((boneKind - 0.34) * 2.0 + lit * 0.18));
    float3 skullColor = lerp(shadow, bone, saturate(lit));
    skullColor = lerp(skullColor, light, saturate(rimLight * (0.14 + max(0.0, uVisualB.w) * 0.035 + flash * 0.40)));
    skullColor = lerp(skullColor, light, saturate(flash * 0.56 + amp * 1.70));
    float skullAlpha = saturate(0.20 + lit * 0.44 + key * 0.28 + rimLight * 0.10 + flash * 0.46);
    o.color = float4(skullColor, skullAlpha);
    o.aux = flash;
  } else if (preset == 7.0) {
    if (input.part > 5.5 && input.part < 6.5) {
      float3 m = input.model;
      float kind = input.param.x;
      float localSeed = hash(input.rnd * 1000.0 + kind * 17.0);
      float rim = smoothstep(0.62, 1.25, length(m.xz));
      float lower = smoothstep(-0.30, -1.28, m.y);
      float laneM = saturate((1.18 - m.y) / 2.95);
      float jellyGroove = saturate(grooveDrive * 0.82 + melodyDrive * 0.36);
      float modelWave = exp(-pow(abs(frac(laneM - frac(uTime * (0.28 + melodyDrive * 0.20) + localSeed) + 0.5) - 0.5) / 0.090, 2.0)) * jellyGroove * 0.18;
      m.xz *= 1.0 + modelWave * (0.016 + lower * 0.032) + jellyGroove * 0.012;
      m.y += sin(uTime * 0.45 + input.rnd * 13.0) * 0.040 - modelWave * (0.036 + laneM * 0.092);
      m.x += sin(uTime * (0.34 + localSeed * 0.16) + input.rnd * 29.0 + m.y * 2.3) * (0.018 + lower * 0.065 + melodyDrive * 0.064 + modelWave * 0.066);
      m.z += cos(uTime * (0.31 + localSeed * 0.18) + input.rnd * 23.0 + m.y * 1.7) * (0.018 + lower * 0.056 + modelWave * 0.058);
      float scaleModel = 0.58 * (1.0 + grooveDrive * 0.052 + melodyDrive * 0.020);
      float yawModel = sin(uTime * 0.045 + localSeed * 6.28) * 0.20;
      float yms = sin(yawModel), ymc = cos(yawModel);
      m.xz = float2(m.x * ymc - m.z * yms, m.x * yms + m.z * ymc);
      p = float2(m.x * scaleModel, m.y * scaleModel + 0.08);
      p.y *= 0.92;
      float shimmer = pow(0.5 + 0.5 * sin(uTime * (1.18 + localSeed * 0.75) + input.rnd * 71.0), 5.0);
      float ribGlow = pow(0.5 + 0.5 * sin(atan2(input.model.z, input.model.x) * 30.0 + input.model.y * 5.0 - uTime * 0.58), 4.0);
      float3 cyan = float3(0.26, 0.86, 1.0);
      float3 pink = float3(1.0, 0.40, 0.95);
      float3 pearl = float3(0.92, 0.98, 1.0);
      float3 jellyColor = lerp(cyan, pink, 0.22 + localSeed * 0.32 + lower * 0.42 + shimmer * 0.18);
      jellyColor = lerp(jellyColor, pearl, saturate(rim * 0.36 + ribGlow * 0.28 + shimmer * 0.22));
      float jellyAlpha = saturate(0.070 + rim * 0.16 + lower * 0.12 + shimmer * 0.13 + smoothstep(2.5, 4.5, kind) * 0.10 + modelWave * 0.18);
      float jellyGlow = 0.28 + rim * 0.72 + ribGlow * 0.62 + shimmer * 0.88 + modelWave * 0.62;
      o.color = float4(jellyColor * (0.72 + jellyGlow * 0.18), jellyAlpha);
      o.aux = jellyGlow;
    } else {
    float jellyCount = 5.0;
    float jelly = floor(input.uv.x * jellyCount);
    float localU = frac(input.uv.x * jellyCount);
    float localV = input.uv.y;
    float mainJelly = 1.0 - step(0.5, abs(jelly - 2.0));
    float2 center = float2(0.0, 0.08);
    float scale = 1.0;
    if (jelly < 0.5) { center = float2(-0.78, -0.12); scale = 0.42; }
    else if (jelly < 1.5) { center = float2(0.72, -0.16); scale = 0.38; }
    else if (jelly < 2.5) { center = float2(0.00, 0.10); scale = 0.84; }
    else if (jelly < 3.5) { center = float2(-0.46, 0.36); scale = 0.30; }
    else { center = float2(0.58, 0.31); scale = 0.26; }
    float jellySeed = hash(jelly * 19.31 + 2.7);
    center.x += sin(uTime * (0.08 + jellySeed * 0.03) + jellySeed * 6.28) * (0.020 + scale * 0.018);
    center.y += sin(uTime * (0.21 + jellySeed * 0.10) + seed) * (0.026 + scale * 0.018) + grooveDrive * (0.028 + mainJelly * 0.028);
    float waveHead = frac(uTime * (0.25 + melodyDrive * 0.18 + jellySeed * 0.04) + jellySeed);
    float section = localV;
    float angleJ = localU * 6.2831853 + jellySeed * 0.55 + sin(uTime * 0.12 + jellySeed) * 0.08;
    float lane = abs(frac(section - waveHead + 0.5) - 0.5);
    float soundWave = exp(-(lane * lane) / max(0.0001, 0.018 + kickRelease * 0.003)) * (grooveDrive * 0.14 + melodyDrive * 0.08);
    if (section < 0.30) {
      float tv = section / 0.30;
      float ring = sqrt(saturate(tv + (hash(seed + 3.0) - 0.5) * 0.035));
      float dome = pow(max(0.0, 1.0 - ring * ring), 0.56);
      float rib = pow(0.5 + 0.5 * sin(angleJ * 28.0 + ring * 8.0 - uTime * (0.38 + slowFlow * .16) + seed), 4.0);
      float rim = smoothstep(0.62, 1.0, ring);
      float radius = (0.54 + rib * 0.025 + soundWave * (0.14 + rim * 0.18)) * (1.0 + grooveDrive * 0.075);
      p = float2(cos(angleJ) * ring * radius * 1.28, dome * 0.34 - ring * ring * 0.11 - soundWave * 0.08);
    } else if (section < 0.44) {
      float tv = (section - 0.30) / 0.14;
      float scallop = pow(0.5 + 0.5 * sin(angleJ * 13.0 + jellySeed * 5.0), 1.65);
      float waveRim = sin(angleJ * 24.0 + uTime * 1.35 + seed);
      float radius = 0.56 + tv * 0.16 + scallop * 0.05 + soundWave * 0.14 + waveRim * 0.025;
      p = float2(cos(angleJ) * radius * 1.22, -0.13 - tv * (0.18 + grooveDrive * 0.06) + waveRim * 0.038 - soundWave * 0.12);
    } else if (section < 0.66) {
      float tv = (section - 0.44) / 0.22;
      float arm = floor(localU * 18.0);
      float armN = (arm + 0.5) / 18.0;
      float armAngle = armN * 6.2831853 + jellySeed * 0.35;
      float2 radial = float2(cos(armAngle), sin(armAngle));
      float2 tangent = float2(-radial.y, radial.x);
      float phase = uTime * (0.72 + melodyDrive * 0.30) + armN * 17.0 + tv * 8.6 + seed;
      float curl = sin(phase) * (0.060 + melodyDrive * 0.18) * (0.4 + tv * 1.1) + sin(phase * 1.7) * 0.035 + soundWave * (0.14 + tv * 0.22);
      float2 baseArm = radial * (0.05 + hash(seed + 4.0) * 0.20) * (1.0 - tv * 0.38) + tangent * curl;
      p = float2(baseArm.x, -0.20 - tv * (0.62 + grooveDrive * 0.12) + sin(phase * .44) * 0.035 - soundWave * 0.12 + baseArm.y * 0.20);
    } else if (section < 0.93) {
      float tv = (section - 0.66) / 0.27;
      float strand = floor(localU * 28.0);
      float strandN = (strand + 0.5) / 28.0;
      float rootR = 0.34 + hash(seed + 6.0) * 0.28;
      float strandAngle = strandN * 6.2831853 + jellySeed * 0.42 + sin(uTime * 0.10 + strandN * 9.0) * 0.08;
      float2 radial = float2(cos(strandAngle), sin(strandAngle));
      float2 tangent = float2(-radial.y, radial.x);
      float phase = uTime * (0.78 + melodyDrive * .34 + jellySeed * .18) + strandN * 18.0 + tv * 10.0;
      float sway = (0.055 + melodyDrive * 0.26) * (0.40 + tv * 1.95);
      float curl = sin(phase) * sway + sin(phase * 1.62 + grooveDrive * 2.0) * 0.055 * tv + sin(phase * 0.43 + tv * 5.0) * 0.16 * tv * tv + soundWave * (0.13 + tv * 0.42);
      float2 baseTentacle = radial * rootR * (0.96 - tv * 0.18) + tangent * (curl + (hash(seed + 11.0) - 0.5) * 0.035);
      p = float2(baseTentacle.x, -0.24 - tv * (1.10 + scale * 0.42 + grooveDrive * 0.24) + sin(phase * .34) * 0.055 - soundWave * (0.12 + tv * 0.22) + baseTentacle.y * 0.16);
    } else {
      float dustT = (section - 0.93) / 0.07;
      float dustAngle = hash(seed + 31.0) * 6.2831853 + uTime * (0.05 + jellySeed * 0.04);
      float rr = lerp(0.18, 0.95, sqrt(hash(seed + 13.0)));
      p = float2(cos(dustAngle) * rr + (hash(seed + 17.0) - 0.5) * 0.30, (hash(seed + 21.0) - 0.5) * 0.92 - dustT * 0.45 + sin(uTime * .23 + seed) * .08);
    }
    float yaw2 = (jellySeed - 0.5) * 0.40 + uTime * (0.008 + jellySeed * 0.010);
    float js = sin(yaw2), jc = cos(yaw2);
    p = float2(p.x * jc - p.y * js * 0.12, p.x * js * 0.12 + p.y * jc);
    p = center + p * scale * (1.0 + grooveDrive * 0.064 + melodyDrive * 0.018);
    p.y *= 0.94;
    float3 cyan = float3(0.26, 0.86, 1.0);
    float3 blue = float3(0.34, 0.48, 1.0);
    float3 pink = float3(1.0, 0.40, 0.95);
    float3 pearl = float3(0.92, 0.98, 1.0);
    float procEdge = smoothstep(0.28, 0.94, section);
    float procSpark = pow(0.5 + 0.5 * sin(uTime * (1.1 + input.rnd) + seed * 7.0), 5.0);
    float3 baseJellyColor = section < 0.44 ? lerp(cyan, pearl, 0.44 + materialDrive * 0.10) : lerp(blue, pink, localU * 0.72 + jellySeed * 0.18);
    baseJellyColor = lerp(baseJellyColor, pearl, procSpark * 0.24 + soundWave * 0.22);
    float procAlpha = section < 0.30 ? 0.18 + (1.0 - section / 0.30) * 0.18 : (section < 0.93 ? 0.12 + (1.0 - procEdge) * 0.20 + procSpark * 0.16 : 0.045 + procSpark * 0.25);
    float procGlow = 0.30 + soundWave * 1.35 + procSpark * 0.58 + materialDrive * 0.08;
    o.color = float4(baseJellyColor * (0.70 + procGlow * 0.16), saturate(procAlpha));
    o.aux = procGlow;
    }
  }
  float mouseBoost = 0.0;
  float rippleBoost = 0.0;
  float mousePresetWeight = preset == 0.0 ? 0.00 : (preset == 1.0 ? 0.16 : (preset == 2.0 ? 0.10 : (preset == 3.0 ? 0.22 : (preset == 4.0 ? 0.42 : (preset == 5.0 ? 0.34 : 0.26)))));
  if (uInteraction.z > 0.5 && mousePresetWeight > 0.01) {
    float2 md = p - uInteraction.xy;
    float dist = length(md);
    float mouseRadius = lerp(0.34, 0.54, saturate(uInteraction.w));
    mouseBoost = pow(saturate(1.0 - dist / mouseRadius), 2.0) * mousePresetWeight;
    float2 outDir = md / max(0.001, dist);
    if (structureLineWeight > 0.01) {
      float lineSign = dot(outDir, structureLineDir) < 0.0 ? -1.0 : 1.0;
      outDir = normalize(lerp(outDir, structureLineDir * lineSign, structureLineWeight));
    }
    float structureProtect = preset == 1.0 ? 0.54 : (preset == 2.0 ? 0.46 : 1.0);
    p += outDir * mouseBoost * structureProtect * (0.034 + sub * 0.018);
    clipDepth = saturate(clipDepth + mouseBoost * structureProtect * 0.042);
    depthScale = saturate(depthScale + mouseBoost * structureProtect * (0.13 + sub * 0.035));
    o.aux = max(o.aux, mouseBoost * 0.82);
  }
  float ripplePresetWeight = preset == 0.0 ? 0.00 : (preset == 1.0 ? 0.12 : (preset == 2.0 ? 0.08 : (preset == 3.0 ? 0.16 : (preset == 4.0 ? 0.32 : (preset == 5.0 ? 0.28 : 0.18)))));
  if (ripplePresetWeight > 0.01) {
    rippleBoost = saturate(rippleSumAt(p) * (0.68 + sub * 0.22) * ripplePresetWeight);
    float2 radial = normalize(p + float2(0.001, -0.002));
    float2 rippleDir = uInteraction.z > 0.5 ? normalize(p - uInteraction.xy + float2(0.001, -0.002)) : radial;
    if (structureLineWeight > 0.01) {
      float lineSign = dot(rippleDir, structureLineDir) < 0.0 ? -1.0 : 1.0;
      rippleDir = normalize(lerp(rippleDir, structureLineDir * lineSign, structureLineWeight));
    }
    float rippleMove = preset == 0.0 ? 1.0 : (preset == 1.0 ? 0.34 : (preset == 2.0 ? 0.32 : (preset == 3.0 ? 0.24 : 0.58)));
    p += rippleDir * rippleBoost * rippleMove * (0.022 + sub * 0.012);
    float rippleDepthProtect = preset == 1.0 ? 0.60 : (preset == 2.0 ? 0.52 : 1.0);
    clipDepth = saturate(clipDepth + rippleBoost * rippleDepthProtect * 0.040);
    depthScale = saturate(depthScale + rippleBoost * rippleDepthProtect * (0.10 + sub * 0.040));
    o.aux = max(o.aux, rippleBoost);
  }
  float handBoost = 0.0;
  float grip = saturate(uHand.w);
  float handPresetWeight = preset == 0.0 ? 0.00 : (preset == 1.0 ? 0.55 : (preset == 2.0 ? 0.18 : (preset == 3.0 ? 0.34 : (preset == 4.0 ? 0.62 : (preset == 5.0 ? 0.50 : 0.42)))));
  if (uHand.z > 0.01) {
    float2 hd = p - uHand.xy;
    float handDist = length(hd);
    float handRadProfile = preset == 0.0 ? 1.42 : (preset == 1.0 ? 0.98 : (preset == 2.0 ? 1.08 : (preset == 3.0 ? 0.78 : (preset == 4.0 ? 1.12 : (preset == 5.0 ? 1.04 : 0.92)))));
    float rad = handRadProfile + grip * 0.18;
    handBoost = pow(saturate((rad - handDist) / rad), 2.0) * saturate(uHand.z) * handPresetWeight;
    float2 handDir = hd / max(0.001, handDist);
    if (structureLineWeight > 0.01) {
      float lineSign = dot(handDir, structureLineDir) < 0.0 ? -1.0 : 1.0;
      handDir = normalize(lerp(handDir, structureLineDir * lineSign, structureLineWeight));
    }
    float handPushScale = preset == 0.0 ? 0.168 : (preset == 2.0 ? 0.078 : (preset == 3.0 ? 0.060 : 0.128));
    p += handDir * handBoost * (handPushScale + sub * 0.035);
    clipDepth = saturate(clipDepth + handBoost * (0.110 + sub * 0.045));
    depthScale = saturate(depthScale + handBoost * (0.19 + sub * 0.050));
    o.aux = max(o.aux, handBoost * 1.05);
  }
  if (grip > 0.001) {
    float gripWave = 0.5 + 0.5 * sin(uTime * 2.2 + input.rnd * 6.2831);
    float gripLocal = preset == 0.0 ? 0.0 : grip * (0.55 + handPresetWeight * 0.45);
    float gripStructureScale = (preset == 1.0 || preset == 2.0) ? 0.18 : 1.0;
    p *= lerp(1.0, 0.66 + gripWave * 0.035, gripLocal * gripStructureScale);
    if (structureLineWeight > 0.01 && gripLocal > 0.001) {
      float lineSign = dot(p, structureLineDir) < 0.0 ? -1.0 : 1.0;
      p += structureLineDir * lineSign * gripLocal * (1.0 - gripStructureScale) * (0.010 + sub * 0.010) * (gripWave - 0.5);
    }
    clipDepth = saturate(clipDepth + gripLocal * (0.052 + sub * 0.044 + gripWave * 0.032));
    depthScale = saturate(depthScale + gripLocal * (0.14 + sub * 0.040));
    o.aux = max(o.aux, gripLocal * (0.72 + gripWave * 0.34));
  }
  float wave = sin(uTime * speed * (0.8 + input.rnd * 2.4) + p.x * 12.0 + p.y * 8.0);
  float swirl = sin(uTime * speed * 0.37 + input.rnd * 6.283);
  float energy = saturate(uEnergy * 0.06 + grooveDrive * 0.42 + melodyDrive * 0.36 + mouseBoost * 0.34 + rippleBoost * 0.28 + handBoost * 0.42 + grip * 0.24);
  float postShape = 0.0;
  float orbitPostFlow = saturate(uAudioA.w * 0.72 + uAudioA.z * 0.38);
  float postWaveXShape = preset == 2.0 ? 0.065 * orbitPostFlow : postShape;
  float postWaveYShape = preset == 2.0 ? 0.028 * orbitPostFlow : postShape;
  float postScaleShape = preset == 2.0 ? 0.18 : postShape;
  float postTwistShape = preset == 2.0 ? 0.026 * orbitPostFlow : postShape;
  float twistDrive = preset == 2.0 ? saturate(max(0.0, twist)) : twist;
  float angle = twistDrive * energy * (0.26 + midFlow * 0.14) * (1.0 - saturate(length(p) * .48)) * postTwistShape;
  float s = sin(angle), c = cos(angle);
  p = float2(p.x * c - p.y * s, p.x * s + p.y * c);
  p.x += (wave * 0.018 * intensity * (0.25 + orbitPostFlow * 0.65) + swirl * 0.006 * orbitPostFlow) * postWaveXShape;
  p.y += cos(uTime * speed * 0.7 + input.rnd * 9.0 + p.x * 10.0) * 0.012 * intensity * (0.2 + orbitPostFlow * 0.45) * postWaveYShape;
  float cinemaParticleJitter = (preset == 0.0 || preset == 1.0 || preset == 2.0) ? 0.0 : 1.0;
  p += float2(sin(uTime * .41 + seed), cos(uTime * .33 + seed * 1.7)) * cinemaShake * 0.003 * cinemaParticleJitter;
  float center = 1.0 - saturate(length(p) * 0.58);
  float uvEdge = max(abs(input.uv.x - 0.5), abs(input.uv.y - 0.5)) * 2.0;
  float edgeGlow = smoothstep(0.62, 1.0, uvEdge) * uVisualC.w * (0.24 + high * 0.38);
  p *= 1.0 + (grooveDrive * 0.070 + scatter * .025 + edgeGlow * 0.018) * center * intensity * postScaleShape;
  o.pos = float4(p, clipDepth, 1.0);
  o.uv = particleUv;
  o.rnd = input.rnd;
  o.depthInfo = saturate(depthScale);
  o.viewDist = max(0.5, -particleViewZ);
  o.lineDir = length(structureLineDir) > 0.001 ? normalize(structureLineDir) : float2(1.0, 0.0);
  float presetSize = preset == 0.0 ? 1.00 : (preset == 1.0 ? 1.00 : (preset == 2.0 ? 0.96 : (preset == 4.0 ? 0.98 : (preset == 5.0 ? 1.00 : (preset == 6.0 ? 1.12 : (preset == 7.0 ? 0.82 : 1.06))))));
  float bloomSize = preset == 5.0 ? lerp(1.0, 1.42, bloomPass) : lerp(1.0, 1.14, bloomPass);
  float spherePointScale = preset == 5.0 ? 1.0 : lerp(1.10, 0.76, bloomPass);
  float depthSize = 36.0 / max(0.5, -particleViewZ);
  float audioBoost = 1.0 + saturate(o.aux) * 0.58 + grooveDrive * 0.08 + melodyDrive * 0.04;
  float playerSz = clamp(depthSize * audioBoost, 1.18, 6.25);
  if (preset == 5.0) {
    float flowDrive = grooveDrive * 0.034 + melodyDrive * 0.046;
    playerSz = clamp(depthSize * (1.05 + flowDrive), 1.00, 5.55);
  } else if (preset == 4.0) {
    float ringDrive = grooveDrive * 0.22 + melodyDrive * 0.08;
    playerSz = clamp(depthSize * (1.10 + ringDrive * 0.56), 4.60, 8.20);
  } else if (preset == 7.0) {
    float jellyDrive = grooveDrive * 0.22 + melodyDrive * 0.18;
    playerSz = clamp(depthSize * (1.10 + jellyDrive), 1.02, 5.95);
  }
  o.size = playerSz * pointScale * presetSize * bloomSize * spherePointScale;
  if (preset == 0.0) {
    float silkGlow = saturate(o.aux);
    float edgeBoost0 = saturate(o.color.g * 0.22);
    float silkAudioBoost = 1.0 + silkGlow * 0.82 + edgeBoost0 * 0.30 + grooveDrive * 0.13 + melodyDrive * 0.085 + accentDrive * 0.11;
    o.size = clamp(depthSize * silkAudioBoost, 0.98, 5.82) * pointScale * presetSize * bloomSize * spherePointScale;
  } else if (preset == 1.0) {
    float tunnelGlow = saturate(o.aux);
    float playerDepthSize = 36.0 / max(0.5, -particleViewZ);
    // Keep tunnel point size close to the player: perspective + ring/ripple
    // energy. midFlow moves the tube and subBass changes radius in the vertex
    // formula; they should not continuously inflate every billboard.
    float tunnelGrooveSize = saturate(uAudioA.x * 0.22 + uAudioA.y * 0.32 + uAudioA.z * 0.24 + uAudioA.w * 0.16);
    float tunnelPulseSize = saturate(max(uAudioC.x * 0.20, tunnelGrooveSize * 0.46));
    float tunnelMacroFlow = saturate(uAudioA.w);
    float tunnelAudioBoost = 1.0 + tunnelGlow * 0.36 + tunnelMacroFlow * 0.030 + tunnelPulseSize * 0.070;
    float tunnelPassSize = lerp(1.0, 0.54, bloomPass);
    o.size = clamp(playerDepthSize * tunnelAudioBoost, 1.05, 4.95) * pointScale * presetSize * tunnelPassSize;
  } else if (preset == 2.0) {
    float playerDepthSize = 36.0 / max(0.5, -particleViewZ);
    // Orbit keeps billboard size stable so the visible contour stays round.
    float orbitShockSize = saturate(o.color.b);
    o.size = clamp(playerDepthSize * (0.96 + orbitPointBoost * 0.22 + orbitShockSize * 0.08), 1.05, 4.18) * pointScale * presetSize * bloomSize * spherePointScale;
  } else if (preset == 3.0) {
    float playerDepthSize = 36.0 / max(0.5, -particleViewZ);
    float voidAir = melodyDrive * 0.18 + grooveDrive * 0.10;
    o.size = clamp(playerDepthSize * (0.56 + voidAir), 0.58, 2.45) * pointScale * 0.76 * lerp(1.0, 0.54, bloomPass);
  } else if (preset == 4.0) {
    float playerDepthSize = 36.0 / max(0.5, -particleViewZ);
    float ringDrive = grooveDrive * 0.22 + melodyDrive * 0.08;
    float tileScale = lerp(0.88, 1.10, saturate(pointScale));
    o.size = clamp(playerDepthSize * (1.10 + ringDrive * 0.56), 4.60, 8.20) * tileScale * lerp(0.92, 1.08, saturate(o.aux)) * lerp(1.0, 0.74, bloomPass);
  } else if (preset == 5.0) {
    float playerDepthSize = 36.0 / max(0.5, -particleViewZ);
    float galaxyDustLane = step(0.80, input.uv.y);
    float galaxyLayerSize = lerp(1.08, 0.48, galaxyDustLane);
    float galaxyPointPass = lerp(1.08, 0.84, bloomPass);
    float galaxySizeBase = playerDepthSize * lerp(1.20, 0.68, galaxyDustLane);
    float galaxyMainSizeBoost = 1.0 + galaxySizeFocus * lerp(0.10, 0.06, bgBright5) * (1.0 - galaxyDustLane);
    o.size = clamp(galaxySizeBase * galaxyMainSizeBoost, lerp(1.24, 0.42, galaxyDustLane), lerp(5.40, 2.35, galaxyDustLane)) * pointScale * presetSize * galaxyPointPass * spherePointScale * galaxyLayerSize;
  } else if (preset == 6.0) {
    float boneKind = frac(input.part);
    float flash = saturate(pow(max(0.0, kick + uBeat - 0.12) / 0.88, 1.22));
    o.size = (1.10 + boneKind * 1.10 + flash * 2.10 + uTreble * 0.55) * pointScale * bloomSize;
  } else if (preset == 7.0 && input.part > 5.5 && input.part < 6.5) {
    float localSeed = hash(input.rnd * 1000.0 + input.param.x * 17.0);
    float shimmer = pow(0.5 + 0.5 * sin(uTime * (1.18 + localSeed * 0.75) + input.rnd * 71.0), 5.0);
    o.size = (1.05 + shimmer * 0.72 + melodyDrive * 0.34 + grooveDrive * 0.30) * pointScale * presetSize * bloomSize;
  }
  return o;
}";

    private const string GeometryShaderSource = @"
cbuffer Params : register(b0) {
  float uTime;
  float uBass;
  float uMid;
  float uTreble;
  float uBeat;
  float uEnergy;
  float uOpacity;
  float uPreset;
  float uWidth;
  float uHeight;
  float uUseTexture;
  float _pad0;
  float4 uPrimary;
  float4 uSecondary;
  float4 uHighlight;
  float4 uVisualA;
  float4 uVisualB;
  float4 uVisualC;
  float4 uVisualD;
  float4 uVisualE;
  float4 uAudioA;
  float4 uAudioB;
  float4 uAudioC;
  float4 uAudioD;
  float4 uRuntime;
  float4 uBackground;
  float4 uInteraction;
  float4 uRipple0;
  float4 uRipple1;
  float4 uRipple2;
  float4 uRipple3;
  float4 uRipple4;
  float4 uRipple5;
  float4 uRipple6;
  float4 uRipple7;
  float4 uRipple8;
  float4 uRipple9;
  float4 uRipple10;
  float4 uRipple11;
  float4 uHand;
  float4 uViewControl;
};
struct VSOut {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float rnd : TEXCOORD1;
  float size : TEXCOORD2;
  float4 color : TEXCOORD3;
  float aux : TEXCOORD4;
  float depthInfo : TEXCOORD5;
  float viewDist : TEXCOORD6;
  float2 lineDir : TEXCOORD7;
  float beat : TEXCOORD8;
};
struct GSOut {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float rnd : TEXCOORD1;
  float2 local : TEXCOORD2;
  float4 color : TEXCOORD3;
  float aux : TEXCOORD4;
  float depthInfo : TEXCOORD5;
  float viewDist : TEXCOORD6;
  float beat : TEXCOORD7;
};
[maxvertexcount(4)]
void main(point VSOut input[1], inout TriangleStream<GSOut> stream) {
  float2 pixel = float2(2.0 / max(uWidth, 1.0), 2.0 / max(uHeight, 1.0)) * input[0].size;
  float preset = floor(uPreset + 0.5);
  // Player presets 1/2 are still round THREE.Points; the line/tube structure
  // comes from particle positions, not from stretching each billboard.
  float lineStretch = 0.0;
  float bloomPass = saturate(_pad0);
  lineStretch *= lerp(1.0, 0.62, bloomPass);
  float2 dirScreen = normalize(float2(input[0].lineDir.x * uWidth, input[0].lineDir.y * uHeight) + float2(0.0001, 0.0));
  float2 dir = normalize(float2(dirScreen.x / max(uWidth, 1.0), dirScreen.y / max(uHeight, 1.0)) + float2(0.000001, 0.0));
  float2 side = float2(-dir.y, dir.x);
  float major = max(pixel.x, pixel.y) * (1.0 + lineStretch);
  float minor = max(pixel.x, pixel.y) * (1.0 - lineStretch * 0.28);
  float2 offsets[4] = { float2(-1.0, -1.0), float2(-1.0, 1.0), float2(1.0, -1.0), float2(1.0, 1.0) };
  float2 locals[4] = { float2(0.0, 0.0), float2(0.0, 1.0), float2(1.0, 0.0), float2(1.0, 1.0) };
  if (preset == 4.0) {
    float tileBoost = lerp(1.86, 2.72, saturate(input[0].aux));
    pixel = max(pixel, float2(2.0 / max(uWidth, 1.0), 2.0 / max(uHeight, 1.0)) * 8.4) * tileBoost;
    major = max(pixel.x, pixel.y);
    minor = major;
  }
  [unroll]
  for (int i = 0; i < 4; i++) {
    GSOut o;
    o.pos = input[0].pos;
    float2 roundOffset = offsets[i] * pixel;
    float2 lineOffset = dir * offsets[i].x * major + side * offsets[i].y * minor;
    o.pos.xy += lerp(roundOffset, lineOffset, saturate(lineStretch));
    o.uv = input[0].uv;
    o.rnd = input[0].rnd;
    o.local = locals[i];
    o.color = input[0].color;
    o.aux = input[0].aux;
    o.depthInfo = input[0].depthInfo;
    o.viewDist = input[0].viewDist;
    o.beat = input[0].beat;
    stream.Append(o);
  }
  stream.RestartStrip();
}";

    private const string PixelShaderSource = @"
cbuffer Params : register(b0) {
  float uTime;
  float uBass;
  float uMid;
  float uTreble;
  float uBeat;
  float uEnergy;
  float uOpacity;
  float uPreset;
  float uWidth;
  float uHeight;
  float uUseTexture;
  float _pad0;
  float4 uPrimary;
  float4 uSecondary;
  float4 uHighlight;
  float4 uVisualA;
  float4 uVisualB;
  float4 uVisualC;
  float4 uVisualD;
  float4 uVisualE;
  float4 uAudioA;
  float4 uAudioB;
  float4 uAudioC;
  float4 uAudioD;
  float4 uRuntime;
  float4 uBackground;
  float4 uInteraction;
  float4 uRipple0;
  float4 uRipple1;
  float4 uRipple2;
  float4 uRipple3;
  float4 uRipple4;
  float4 uRipple5;
  float4 uRipple6;
  float4 uRipple7;
  float4 uRipple8;
  float4 uRipple9;
  float4 uRipple10;
  float4 uRipple11;
  float4 uHand;
  float4 uViewControl;
};
Texture2D uCover : register(t0);
Texture2D uEdge : register(t1);
SamplerState uCoverSampler : register(s0);
float hash(float n) { return frac(sin(n) * 43758.5453); }
struct PSIn {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float rnd : TEXCOORD1;
  float2 local : TEXCOORD2;
  float4 color : TEXCOORD3;
  float aux : TEXCOORD4;
  float depthInfo : TEXCOORD5;
  float viewDist : TEXCOORD6;
  float beat : TEXCOORD7;
};
float dotTextureAlpha(float r) {
  r = saturate(r);
  if (r < 0.42) return lerp(0.96, 0.78, r / 0.42);
  if (r < 0.72) return lerp(0.78, 0.22, (r - 0.42) / 0.30);
  return lerp(0.22, 0.0, (r - 0.72) / 0.28);
}
float4 sampleCoverTile(float2 baseUv, float2 local, float tileSpan, float strength, float bloomPass) {
  float2 tileLocal = local - 0.5;
  float2 tileStep = float2(tileSpan, tileSpan);
  float2 tileUv = clamp(baseUv + tileLocal * tileStep * 4.25, 0.0, 1.0);
  float2 fineUv = clamp(baseUv + tileLocal * tileStep * 1.55, 0.0, 1.0);
  float4 center = uCover.Sample(uCoverSampler, baseUv);
  float4 tile = uCover.Sample(uCoverSampler, tileUv);
  float4 fine = uCover.Sample(uCoverSampler, fineUv);
  float localInk = dot(abs(tile.rgb - center.rgb), float3(0.299, 0.587, 0.114));
  float localMix = saturate(uUseTexture) * saturate(strength) * lerp(0.58, 0.78, smoothstep(0.025, 0.18, localInk)) * (1.0 - bloomPass * 0.42);
  return lerp(center, lerp(fine, tile, 0.62), localMix);
}
float galaxyBeatWaveOne(float centerDist, float age, float strength) {
  if (strength < 0.010 || age < 0.0 || age > 4.60) return 0.0;
  float life = saturate(age / 4.40);
  float radius = lerp(0.01, 0.72, pow(life, 1.08));
  float width = lerp(0.045, 0.085, life);
  float delta = centerDist - radius;
  float head = exp(-delta * delta / max(0.0001, width * width));
  float tailDelta = delta + width * 1.55;
  float tail = exp(-tailDelta * tailDelta / max(0.0001, width * width * 4.4))
    * (1.0 - smoothstep(0.0, width * 1.55, delta));
  float fade = smoothstep(0.0, 0.16, age) * (1.0 - smoothstep(3.60, 4.45, age));
  return strength * fade * (head * 0.86 + tail * 0.15);
}
float galaxyBeatWaveSum(float centerDist) {
  float sum = 0.0;
  sum += galaxyBeatWaveOne(centerDist, uAudioA.x, uAudioB.x);
  sum += galaxyBeatWaveOne(centerDist, uAudioA.y, uAudioB.y);
  sum += galaxyBeatWaveOne(centerDist, uAudioA.z, uAudioB.z);
  sum += galaxyBeatWaveOne(centerDist, uAudioA.w, uAudioB.w);
  return min(sum, 1.35);
}
float tunnelPackedPulseStrength(float packed) {
  return floor(max(0.0, packed)) * 0.01;
}
float tunnelPackedPulseTravel(float packed) {
  return frac(max(0.0, packed));
}
float tunnelPackedPulseLife(float travel) {
  return smoothstep(0.0, 0.035, travel) * (1.0 - smoothstep(0.92, 1.0, travel));
}
float tunnelEventPulseRing(float packed, float band, float baseWidth) {
  float strength = tunnelPackedPulseStrength(packed);
  if (strength <= 0.01) return 0.0;
  float travel = tunnelPackedPulseTravel(packed);
  float fullStrength = smoothstep(1.24, 1.34, strength);
  float visibleTravel = saturate(travel / 0.94);
  float mappedTravel = lerp(saturate(travel), visibleTravel, fullStrength);
  float head = lerp(0.18, 1.00, mappedTravel);
  float exitBrightness = smoothstep(0.18, 0.56, head) * fullStrength;
  float width = max(0.010, baseWidth * (0.58 + strength * 0.08) * (1.0 - exitBrightness * 0.42));
  float fullGain = lerp(1.0, 0.42 + exitBrightness * 2.05, fullStrength);
  return exp(-pow((band - head) / width, 2.0)) * strength * tunnelPackedPulseLife(travel) * fullGain;
}
float4 main(PSIn input) : SV_TARGET {
  float bloomPass = saturate(_pad0);
  float preset = floor(uPreset + 0.5);
  bool vinylTerrainTile = preset == 4.0;
  float2 d = input.local * 2.0 - 1.0;
  float r2 = dot(d, d);
  if (r2 > 1.0 && !vinylTerrainTile) discard;
  float radial = sqrt(saturate(r2));
  float dotAlpha = dotTextureAlpha(radial);
  if (vinylTerrainTile) {
    float squareRadius = max(abs(d.x), abs(d.y));
    float bevel = 1.0 - smoothstep(0.84, 1.0, squareRadius);
    float gridInset = smoothstep(1.0, 0.90, squareRadius);
    dotAlpha = saturate(0.72 + bevel * 0.28) * gridInset;
    radial = squareRadius;
    r2 = squareRadius * squareRadius;
  }
  if (dotAlpha < lerp(0.02, 0.01, bloomPass)) discard;
  float dotSoft = dotAlpha * dotAlpha;
  float readableRim = smoothstep(0.44, 0.94, radial) * (1.0 - smoothstep(0.94, 1.08, radial)) * dotAlpha;
  float falloff = saturate(1.0 - r2);
  falloff = lerp(falloff * falloff, pow(falloff, 1.35), bloomPass);
  float3 sphereN = normalize(float3(d.x * 0.96, -d.y * 0.96, sqrt(saturate(1.0 - r2))));
  float sphereLight = pow(saturate(dot(sphereN, normalize(float3(-0.50, 0.64, 0.58)))), 0.78);
  float sphereFill = saturate(dot(sphereN, normalize(float3(0.30, -0.22, 0.70))));
  float sphereSpec = pow(saturate(dot(reflect(normalize(float3(0.34, -0.50, -0.80)), sphereN), float3(0.0, 0.0, 1.0))), 34.0) * (1.0 - smoothstep(0.56, 0.96, radial));
  float sphereCore = 1.0 - smoothstep(0.18, 0.58, radial);
  float sphereEdge = smoothstep(0.56, 0.98, radial);
  float sphereAlpha = saturate(dotAlpha * (0.98 + sphereCore * 0.10 + sphereLight * 0.045 - sphereEdge * 0.10));
  float sphereRim = smoothstep(0.58, 0.94, radial) * (1.0 - smoothstep(0.90, 1.0, radial));
  float outerGlowAlpha = max(sphereRim * dotAlpha * 0.13, smoothstep(0.66, 0.94, radial) * dotSoft * 0.075);
  float band = 0.5 + 0.5 * sin(input.uv.x * 16.0 + uTime * 0.8 + input.rnd * 6.283);
  float sparkle = max(0.0, uVisualE.y);
  float airDust = max(0.0, uVisualE.z);
  float edgePeak = max(0.0, uVisualE.x);
  float kick = clamp(max(uAudioC.x * max(0.05, uVisualE.w) * max(0.35, uAudioC.z), uBeat * 0.20), 0.0, 1.45);
  float midFlow = clamp(max(uAudioA.w * uVisualD.w, uMid * 0.24), 0.0, 1.25);
  float presence = clamp(max(uAudioB.y * edgePeak, uTreble * 0.18), 0.0, 1.25);
  float spark = clamp(max(max(uAudioB.z * sparkle, uAudioB.w * airDust), uTreble * 0.16), 0.0, 1.35);
  float air = clamp(max(uAudioB.w * airDust, uTreble * 0.14), 0.0, 1.25);
  float tunnelSubPx = clamp(uAudioA.x, 0.0, 1.35);
  float tunnelBodyPx = clamp(uAudioA.y, 0.0, 1.35);
  float tunnelLowMidPx = clamp(uAudioA.z, 0.0, 1.25);
  if (preset == 0.0) {
    edgePeak = clamp(uTreble * 0.58, 0.0, 1.25);
    sparkle = clamp(uTreble, 0.0, 1.35);
    airDust = clamp(uTreble * 0.34, 0.0, 1.25);
    kick = clamp(uBeat, 0.0, 1.45);
    midFlow = clamp(uMid, 0.0, 1.25);
    presence = clamp(uTreble * 0.58, 0.0, 1.25);
    spark = sparkle;
    air = airDust;
  } else if (preset == 1.0) {
    kick = clamp(uAudioC.x, 0.0, 1.45);
    tunnelSubPx = clamp(uAudioA.x, 0.0, 1.35);
    tunnelBodyPx = clamp(uAudioA.y, 0.0, 1.35);
    tunnelLowMidPx = clamp(uAudioA.z, 0.0, 1.25);
    midFlow = clamp(uAudioA.w, 0.0, 1.25);
    presence = clamp(uAudioB.y, 0.0, 1.25);
    spark = 0.0;
    air = 0.0;
  } else if (preset == 2.0) {
    kick = clamp(max(uAudioC.x * max(0.45, uVisualE.w) * max(0.55, uAudioC.z), uBeat * 0.12), 0.0, 1.45);
    midFlow = clamp(max(uAudioA.w, uMid * 0.22), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.10), 0.0, 1.25);
    spark = clamp(max(uAudioB.w * 0.24, uTreble * 0.05), 0.0, 1.35);
    air = clamp(max(uAudioB.w * 0.36, uTreble * 0.06), 0.0, 1.25);
  } else if (preset == 3.0) {
    kick = clamp(max(uAudioC.x * 0.06, uBeat * 0.04), 0.0, 1.45);
    midFlow = clamp(max(uAudioA.w * 0.16, uMid * 0.08), 0.0, 1.25);
    presence = clamp(max(uAudioB.y * 0.10, uTreble * 0.05), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.08, uAudioB.w * 0.06), 0.0, 1.35);
    air = clamp(max(uAudioB.w, uTreble * 0.18), 0.0, 1.25);
  } else if (preset == 4.0) {
    kick = clamp(max(uAudioC.x * max(0.52, uVisualE.w) * max(0.70, uAudioC.z), uBeat * 0.14), 0.0, 1.45);
    midFlow = clamp(max(uAudioA.w, uMid * 0.18), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.10), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.58, uAudioB.w * 0.16), 0.0, 1.35);
    air = clamp(max(uAudioB.w * 0.22, uTreble * 0.04), 0.0, 1.25);
  } else if (preset == 5.0) {
    kick = clamp(max(uAudioC.x * 0.32 * max(0.62, uAudioC.z), uBeat * 0.06), 0.0, 1.45);
    midFlow = clamp(max(uAudioA.w, uMid * 0.16), 0.0, 1.25);
    presence = clamp(max(uAudioB.y * 0.34, uTreble * 0.06), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.18, uAudioB.w * 0.24), 0.0, 1.35);
    air = clamp(max(uAudioB.w, uTreble * 0.08), 0.0, 1.25);
  } else if (preset == 7.0) {
    kick = clamp(max(uAudioC.x * 0.46 * max(0.60, uVisualE.w) * max(0.72, uAudioC.z), uBeat * 0.10), 0.0, 1.45);
    midFlow = clamp(max(uAudioA.w, uMid * 0.14), 0.0, 1.25);
    presence = clamp(max(uAudioB.y, uTreble * 0.10), 0.0, 1.25);
    spark = clamp(max(uAudioB.z * 0.46, uAudioB.w * 0.18), 0.0, 1.35);
    air = clamp(max(uAudioB.w, uTreble * 0.08), 0.0, 1.25);
  }
  float bodyPulse = clamp(max(uAudioA.y, uBass * 0.24), 0.0, 1.25);
  float melodyPulse = clamp(max(uAudioA.z * 0.34 + uAudioA.w * 0.56, uMid * 0.26), 0.0, 1.25);
  float detailPulse = clamp(presence * 0.18 + spark * 0.10 + air * 0.12, 0.0, 0.55);
  float pulse = saturate(kick * 0.22 + bodyPulse * 0.18 + melodyPulse * 0.12 + detailPulse * (0.05 + band * 0.025));
  if (preset == 0.0) {
    // Cover particles should read as stable Z breathing, not a full-surface
    // beat flash.
    pulse = saturate(kick * 0.06 + bodyPulse * 0.16 + melodyPulse * 0.035 + detailPulse * 0.030);
  } else if (preset == 1.0) {
    // Tunnel keeps impact on local rings; material brightness follows structure
    // and flow more than kick.
    pulse = saturate(kick * 0.18 + bodyPulse * 0.14 + melodyPulse * 0.10 + detailPulse * 0.035);
  } else if (preset == 2.0) {
    pulse = saturate(kick * 0.14 + bodyPulse * 0.18 + melodyPulse * 0.075 + detailPulse * 0.030);
  } else if (preset == 4.0) {
    pulse = saturate(kick * 0.20 + bodyPulse * 0.12 + melodyPulse * 0.085 + detailPulse * 0.055);
  } else if (preset == 5.0) {
    // Galaxy should drift and breathe; impact only adds a tiny glint.
    pulse = saturate(kick * 0.035 + bodyPulse * 0.11 + melodyPulse * 0.055 + detailPulse * 0.060);
  } else if (preset == 7.0) {
    pulse = saturate(kick * 0.13 + bodyPulse * 0.16 + melodyPulse * 0.14 + detailPulse * 0.050);
  }
  float depthNear = 1.0 - smoothstep(6.0, 24.0, input.viewDist);
  float depthFar = smoothstep(18.0, 44.0, input.viewDist);
  float materialDepthSolid = saturate(0.76 + depthNear * 0.30 - depthFar * 0.18);
  float materialDepthGlow = saturate(0.82 + depthNear * 0.16 - depthFar * 0.34);
  if (preset == 5.0 && input.color.a <= 0.001) discard;
  if (preset == 0.0 && input.color.a > 0.0) {
    float4 coverSample = sampleCoverTile(input.uv, input.local, 1.0 / 183.0, 1.0, bloomPass);
    float3 sampled = coverSample.rgb;
    float sampleAlpha = lerp(1.0, saturate(coverSample.a), saturate(uUseTexture));
    float4 edgeMap = uEdge.Sample(uCoverSampler, input.uv);
    float depthVal = edgeMap.r;
    float edgeVal = edgeMap.g;
    float fgMask = edgeMap.b;
    float lumVal = edgeMap.a;
    float depthAlpha = saturate(input.color.a);
    float depthBright = saturate(input.color.r);
    float glow = saturate(input.aux);
    float3 theme = lerp(uPrimary.rgb, uSecondary.rgb, input.uv.y);
    sampled = pow(max(sampled, float3(0.0, 0.0, 0.0)), float3(0.92, 0.92, 0.92));
    float3 coverColor = lerp(theme, sampled, saturate(uUseTexture));
    float sampleMax = max(max(sampled.r, sampled.g), sampled.b);
    float sampleMin = min(min(sampled.r, sampled.g), sampled.b);
    float sampleChroma = sampleMax - sampleMin;
    float bgConfidence = saturate(uBackground.w);
    float brightBg = smoothstep(0.44, 0.86, saturate(uBackground.x)) * bgConfidence;
    float midBg = smoothstep(0.24, 0.52, saturate(uBackground.x)) * (1.0 - brightBg * 0.48) * bgConfidence;
    float contrastBg = saturate(uBackground.y) * bgConfidence;
    float busyBg = smoothstep(0.22, 0.66, max(contrastBg, saturate(uBackground.z))) * bgConfidence;
    float lowContrastBg = (1.0 - smoothstep(0.16, 0.50, contrastBg)) * smoothstep(0.20, 0.72, saturate(uBackground.x)) * bgConfidence;
    float bgRead = saturate(max(max(brightBg, midBg * 0.62), max(busyBg * 0.70, lowContrastBg * 0.48)));
    float edgeDrive = saturate(edgeVal * (0.38 + edgePeak * 0.26 + presence * 0.18));
    float rippleDrive = saturate(glow * 1.5);
    float cursorGlow = saturate(glow);
    float keepBlack = 1.0 - smoothstep(0.025, 0.115, lumVal);
    float nonBlack = 1.0 - keepBlack;
    float lumPresence = smoothstep(0.08, 0.62, lumVal);
    float darkInk = 1.0 - smoothstep(0.18, 0.70, lumVal);
    float whitePaper = smoothstep(0.72, 0.96, sampleMax) * (1.0 - smoothstep(0.045, 0.18, sampleChroma));
    float inkPresence = saturate(sampleChroma * 1.04 + edgeDrive * 0.36 + fgMask * 0.46 + darkInk * 0.30);
    float subjectInk = saturate(edgeDrive * 0.84 + sampleChroma * 1.18 + fgMask * 0.52 + darkInk * 0.48 + rippleDrive * 0.03);
    float detailInk = smoothstep(0.10, 0.62, subjectInk);
    float paperDetailKeep = saturate(edgeDrive * 2.6 + sampleChroma * 4.4 + (1.0 - whitePaper) * (fgMask * 0.70 + darkInk * 0.50));
    float paperOnly = whitePaper * (1.0 - paperDetailKeep);
    float alphaShape = lerp(1.0, saturate(sampleAlpha + edgeDrive * 0.22 + rippleDrive * 0.08), saturate(uUseTexture));
    float paperLumGate = lerp(1.0, 0.34, paperOnly * saturate(uUseTexture));
    float subjectPresence = saturate(fgMask * 0.17 + edgeDrive * 0.27 + lumPresence * lerp(0.13, 0.026, bgRead) * paperLumGate + inkPresence * 0.54 + rippleDrive * 0.08) * alphaShape;
    float shapedPresence = pow(subjectPresence, lerp(1.06, 1.34, bgRead));
    float basePresence = lerp(0.010, 0.0012, bgRead) * lerp(1.0, 0.06, saturate(uUseTexture));
    float paperSuppress = paperOnly * lerp(0.52, 0.90, bgRead);
    float brightSubjectGate = lerp(1.0, lerp(0.035, 1.0, detailInk), bgRead);
    float whitePaperGate = lerp(1.0, lerp(0.055, 0.015, bgRead), paperOnly * saturate(uUseTexture));
    float lowDetailFade = lerp(1.0, lerp(0.32, 1.0, detailInk), bgRead);
    float lowDetailSubjectGate = lerp(1.0, lerp(0.12, 1.0, detailInk), bgRead * saturate(uUseTexture));
    float paperBackgroundGate = lerp(1.0, 0.004, paperOnly * saturate(uUseTexture));
    float planeEdge = smoothstep(0.58, 0.98, max(abs(input.uv.x - 0.5), abs(input.uv.y - 0.5)) * 2.0);
    float lowDetailPlane = saturate((1.0 - detailInk) * (1.0 - edgeDrive * 0.82) * (1.0 - sampleChroma * 2.35));
    float planeEdgeGate = lerp(1.0, lerp(0.26, 0.080, bgRead), planeEdge * lowDetailPlane * saturate(uUseTexture));
    float interiorPaperGate = lerp(1.0, lerp(0.72, 0.40, bgRead), paperOnly * lowDetailPlane * saturate(uUseTexture));
    float backgroundOnly = saturate((1.0 - detailInk) * (1.0 - edgeDrive * 0.88) * (1.0 - fgMask * 1.12) * (1.0 - sampleChroma * 2.20));
    float backgroundOnlyGate = lerp(1.0, lerp(0.22, 0.045, bgRead), backgroundOnly * saturate(uUseTexture));
    float hardPaperGate = lerp(1.0, 0.0008, paperOnly * saturate(uUseTexture));
    float subjectGatePresence = alphaShape
      * lerp(0.88, 1.0, saturate(uUseTexture))
      * lerp(1.0, 0.76, paperOnly * bgRead)
      * lerp(1.0, 0.82, backgroundOnly * bgRead)
      * lerp(1.0, 0.90, planeEdge * lowDetailPlane * bgRead * saturate(uUseTexture));
    float coverGrid = saturate(uRuntime.z);
    float fullGridPresence = alphaShape
      * lerp(0.92, 1.0, saturate(uUseTexture))
      * lerp(1.0, 0.88, bgRead)
      * lerp(1.0, lerp(0.56, 0.34, bgRead), paperOnly * lowDetailPlane * saturate(uUseTexture))
      * lerp(1.0, 0.74, planeEdge * lowDetailPlane * bgRead * saturate(uUseTexture));
    float coverPresence = lerp(subjectGatePresence, fullGridPresence, coverGrid);
    float desktopSubjectBoost = 1.0 + lowContrastBg * 0.10;
    float darkGuard = saturate(keepBlack * (1.0 - fgMask) * lerp(0.34, 0.095, coverGrid) * saturate(uUseTexture));
    float coverGamma = 1.0 / max(0.35, uVisualB.z);
    coverColor = pow(max(coverColor, float3(0.0, 0.0, 0.0)), float3(coverGamma, coverGamma, coverGamma));
    coverColor = lerp(coverColor, coverColor + float3(0.035, 0.035, 0.035), edgeDrive * 0.055 * (1.0 - darkGuard));
    coverColor = lerp(coverColor, uHighlight.rgb * max(0.14, max(max(coverColor.r, coverColor.g), coverColor.b) * 0.74), saturate(uVisualC.z) * 0.0035 * (1.0 - darkGuard));
    float3 desktopInk = lerp(float3(0.045, 0.14, 0.16), float3(0.10, 0.24, 0.27), saturate(edgeDrive + sampleChroma * 0.65 + depthVal * 0.25));
    desktopInk = lerp(desktopInk, float3(0.070, 0.18, 0.20), lowContrastBg * 0.38);
    float coverLum = dot(coverColor, float3(0.299, 0.587, 0.114));
    float3 coverChroma = lerp(float3(coverLum, coverLum, coverLum), coverColor, 1.0 + sampleChroma * 0.44 + edgeDrive * 0.14);
    float subjectColorKeep = saturate(shapedPresence + sampleChroma * 0.70 + edgeDrive * 0.46);
    float3 desktopContrastCover = lerp(desktopInk * 0.42, coverChroma, saturate(0.62 + subjectColorKeep * 0.38));
    coverColor = lerp(coverColor, desktopContrastCover, bgRead * lerp(0.24, 0.08, coverGrid));
    float musicBreath = saturate(bodyPulse * 0.22 + melodyPulse * 0.10 + kick * 0.13 + glow * 0.10);
    float subjectBright = lerp(0.84, 0.76 + detailInk * 0.10 + edgeDrive * 0.05, bgRead) + rippleDrive * 0.075 + edgeDrive * 0.052 + subjectPresence * 0.030 + musicBreath * 0.070;
    float gridBright = lerp(0.82, 0.70, bgRead) + rippleDrive * 0.060 + edgeDrive * 0.040 + subjectPresence * 0.014 + musicBreath * 0.052;
    float coverBright = lerp(subjectBright, gridBright, coverGrid);
    float coverCore = dotAlpha;
    float alphaMain = saturate(depthAlpha * uOpacity * max(0.12, uVisualA.x) * coverCore * coverPresence * materialDepthSolid * (1.0 - darkGuard * 0.10) * lerp(1.0, 0.98, bgRead) * desktopSubjectBoost);
    float alphaBloom = saturate(depthAlpha * uOpacity * max(0.12, uVisualA.x) * dotSoft * max(0.0, uVisualB.w) * coverPresence * (0.00032 + rippleDrive * 0.00072 + edgeDrive * 0.00062 + cursorGlow * 0.00046 + musicBreath * 0.00026) * materialDepthGlow * (1.0 - darkGuard * 0.34) * lerp(1.0, lerp(0.30, 0.64, coverGrid), bgRead));
    float alpha = lerp(alphaMain, alphaBloom, bloomPass);
    float3 lit = coverColor * coverBright;
    lit = lerp(lit, lit * 1.035 + float3(0.008, 0.008, 0.008), edgeDrive * 0.055);
    lit = lerp(lit, lit * 1.045, rippleDrive * 0.10);
    lit = lerp(lit, lit * 1.055 + float3(0.006, 0.006, 0.006), musicBreath * 0.055);
    float3 bloomLit = coverColor * (0.12 + coverBright * 0.18);
    bloomLit = lerp(bloomLit, bloomLit + float3(0.045, 0.034, 0.020), edgeDrive * 0.045);
    float outLum = dot(lit, float3(0.299, 0.587, 0.114));
    float lightParticle = smoothstep(0.50, 0.82, outLum) * nonBlack;
    float darkParticle = (1.0 - smoothstep(0.20, 0.50, outLum)) * nonBlack;
    lit = lerp(lit, float3(0.0, 0.0, 0.0), readableRim * lightParticle * lerp(0.16, 0.42, bgRead));
    lit = lerp(lit, float3(1.0, 1.0, 1.0), readableRim * darkParticle * 0.035);
    lit = clamp(lit, float3(0.0, 0.0, 0.0), float3(1.6, 1.6, 1.6));
    bloomLit = clamp(bloomLit, float3(0.0, 0.0, 0.0), float3(1.8, 1.8, 1.8));
    lit = lerp(lit, bloomLit, bloomPass);
    lit *= (0.84 + shapedPresence * 0.08 + depthNear * 0.05 - depthFar * 0.08) * (1.0 - darkGuard * 0.10) * lerp(1.0, 0.86, bgRead);
    return float4(lit * alpha, alpha);
  }
  if (preset == 1.0 && input.color.a > 0.0) {
    float depthAlpha = saturate(input.color.a);
    float kickRing = saturate(input.color.g);
    float macroPulsePx = saturate(uAudioC.x * 0.42);
    float depthLayer = saturate(input.depthInfo);
    float tunnelBand = saturate(input.color.b);
    float purpleBlueBlock = 1.0 - smoothstep(0.44, 0.58, tunnelBand);
    float purpleRegionMask = saturate(input.beat);
    float purplePulseMask = smoothstep(0.42, 0.78, purpleRegionMask);
    float rhythmMask = smoothstep(0.20, 0.64, purpleRegionMask);
    float blueColorMask = saturate(smoothstep(0.30, 0.54, tunnelBand) * (1.0 - smoothstep(0.58, 0.70, tunnelBand)));
    float glow = saturate(input.aux);
    float bgConfidence = saturate(uBackground.w);
    float bgLuma = saturate(uBackground.x);
    float bgDetail = max(saturate(uBackground.y), saturate(uBackground.z * 0.72 + uBackground.y * 0.28));
    float brightBg = smoothstep(0.34, 0.78, bgLuma) * bgConfidence;
    float busyBg = smoothstep(0.18, 0.64, bgDetail + uBackground.z * 0.18) * bgConfidence;
    float brightInkAdapt = saturate(max(brightBg, busyBg * 0.46));
    float bgRead = saturate(brightBg * 0.74 + busyBg * 0.36);
    float3 sampled = sampleCoverTile(input.uv, input.local, 1.0 / 167.0, 0.58, bloomPass).rgb;
    float4 edgeMap = uEdge.Sample(uCoverSampler, input.uv);
    float edgeVal = edgeMap.g;
    float lumVal = edgeMap.a;
    float3 tunnelNearColor = lerp(float3(0.32, 0.96, 1.08), float3(0.50, 0.90, 1.18), brightInkAdapt * 0.42);
    float3 tunnelFarColor = lerp(float3(0.62, 0.40, 1.16), float3(0.78, 0.48, 1.24), brightInkAdapt);
    float3 tunnelAccentTheme = lerp(tunnelFarColor, tunnelNearColor, tunnelBand);
    float3 innerDarkPurpleTheme = lerp(float3(0.34, 0.13, 0.66), float3(0.50, 0.22, 0.88), brightInkAdapt);
    float3 midBrightPurpleTheme = lerp(float3(0.88, 0.40, 1.24), float3(1.02, 0.52, 1.36), brightInkAdapt);
    float3 outerBlueTheme = lerp(float3(0.30, 0.66, 0.96), float3(0.50, 0.86, 1.14), brightInkAdapt);
    float purpleDepthRamp = pow(saturate((tunnelBand + 0.035) / 0.24), 0.50);
    float cyanDepthRamp = smoothstep(0.34, 0.56, tunnelBand);
    float3 tunnelBaseTheme = lerp(innerDarkPurpleTheme, midBrightPurpleTheme, purpleDepthRamp);
    tunnelBaseTheme = lerp(tunnelBaseTheme, outerBlueTheme, cyanDepthRamp * 0.82);
    float3 tunnelTheme = lerp(tunnelBaseTheme, tunnelAccentTheme, blueColorMask * 0.54);
    // Player tunnel samples the current cover at vec2(aUv.x, flow); the cyan/purple
    // theme is only a fallback when there is no usable texture.
    float textureTint = saturate(uUseTexture);
    float3 tunnelColor = lerp(tunnelTheme, sampled, textureTint * 0.18);
    float tunnelGamma = 1.0 / max(0.35, uVisualB.z);
    tunnelColor = pow(max(tunnelColor, float3(0.0, 0.0, 0.0)), float3(tunnelGamma, tunnelGamma, tunnelGamma));
    float sampledMax = max(max(sampled.r, sampled.g), sampled.b);
    float brightCoverGuard = smoothstep(0.58, 0.92, sampledMax) * textureTint;
    float darkCoverGuard = (1.0 - smoothstep(0.030, 0.185, sampledMax)) * textureTint;
    tunnelColor = lerp(tunnelColor, tunnelTheme * (0.58 + tunnelBand * 0.34), darkCoverGuard * 0.28);
    tunnelColor = lerp(tunnelColor, tunnelTheme * (0.64 + tunnelBand * 0.30), brightCoverGuard * 0.18);
    tunnelColor = lerp(tunnelColor, tunnelColor + float3(0.06, 0.06, 0.06), saturate(edgeVal * 0.08) * (1.0 - textureTint * 0.82));
    float tunnelT = uTime * max(0.08, uVisualA.y);
    float breathPhase = frac(uRuntime.z);
    float deepBreath = 0.5 - 0.5 * cos(breathPhase * 6.2831853);
    float breathPulseTravel = frac(breathPhase + 0.50);
    float tunnelGroovePx = saturate(tunnelSubPx * 0.30 + tunnelBodyPx * 0.40 + tunnelLowMidPx * 0.28 + midFlow * 0.18);
    float kickBeat = smoothstep(0.06, 0.78, kick);
    float grooveBeat = smoothstep(0.12, 0.76, tunnelGroovePx);
    float macroMotionPx = saturate(uAudioD.y);
    float macroStructurePx = saturate(uAudioD.x);
    float macroImpactPx = saturate(uAudioD.w);
    float outerAccent = saturate(uAudioC.y);
    float flowBeat = smoothstep(0.16, 0.82, saturate(tunnelLowMidPx * 0.32 + midFlow * 0.38 + macroMotionPx * 0.26 + presence * 0.08));
    float liftBeat = smoothstep(0.18, 0.72, saturate(macroMotionPx * 0.42 + macroStructurePx * 0.18 + tunnelLowMidPx * 0.20 + midFlow * 0.16));
    float ringBeat = saturate(max(kickBeat, max(grooveBeat * 0.86, flowBeat * 0.78)) + min(kickBeat, max(grooveBeat, flowBeat)) * 0.16 + liftBeat * 0.12);
    float blueRingGate = smoothstep(0.24, 0.76, saturate(outerAccent * 0.42 + flowBeat * liftBeat * 0.26 + kick * 0.14 + macroImpactPx * 0.08 + macroPulsePx * 0.04));
    float heavyPulseGate = smoothstep(0.32, 0.86, saturate(outerAccent * 0.56 + kick * 0.46 + macroImpactPx * 0.34 + flowBeat * liftBeat * 0.18 + macroPulsePx * 0.05));
    float purpleRegionLevel = purplePulseMask * saturate(0.24 + grooveBeat * 0.30 + flowBeat * 0.25 + ringBeat * 0.18 + presence * 0.10);
    kickRing *= purpleBlueBlock;
    float standingRing = exp(-pow((tunnelBand - 0.52) / 0.090, 2.0)) * (0.095 + ringBeat * 0.36 + macroPulsePx * 0.025) * purpleBlueBlock;
    float sourceBreath = saturate(0.080 + deepBreath * (0.23 + flowBeat * 0.055) + ringBeat * 0.22 + macroPulsePx * 0.024);
    float breathPulseActive = (1.0 - smoothstep(0.72, 1.0, breathPulseTravel)) * saturate(0.52 + sourceBreath + kickBeat * 0.14 + flowBeat * 0.10 + liftBeat * 0.08);
    float breathPulseHead = lerp(0.52, 0.96, breathPulseTravel);
    float latePulseTravel = smoothstep(0.50, 0.94, breathPulseTravel);
    float breathPulseRing = exp(-pow((tunnelBand - breathPulseHead) / 0.060, 2.0)) * breathPulseActive * saturate(0.72 + sourceBreath * 0.28 + ringBeat * 0.17) * purpleBlueBlock;
    float deepPurpleBreath = exp(-pow((tunnelBand - 0.34) / 0.20, 2.0)) * (0.12 + deepBreath * 0.34 + ringBeat * 0.08) * purpleBlueBlock;
    float darkPurpleDeformMask = 0.0;
    float brightPurpleBreath = 0.0;
    float eventTravel0 = tunnelPackedPulseTravel(uAudioB.z);
    float eventTravel1 = tunnelPackedPulseTravel(uRuntime.w);
    float eventRing0 = tunnelEventPulseRing(uAudioB.z, tunnelBand, 0.046);
    float eventRing1 = tunnelEventPulseRing(uRuntime.w, tunnelBand, 0.046);
    float eventStrength0 = tunnelPackedPulseStrength(uAudioB.z);
    float eventStrength1 = tunnelPackedPulseStrength(uRuntime.w);
    float strongEvent0 = smoothstep(0.94, 1.14, eventStrength0);
    float strongEvent1 = smoothstep(0.94, 1.14, eventStrength1);
    float blueSlotScore0 = strongEvent0 * eventStrength0 * tunnelPackedPulseLife(eventTravel0) * (0.82 + smoothstep(0.48, 0.82, eventTravel0) * 0.28);
    float blueSlotScore1 = strongEvent1 * eventStrength1 * tunnelPackedPulseLife(eventTravel1) * (0.82 + smoothstep(0.48, 0.82, eventTravel1) * 0.28);
    float blueSlot0 = step(blueSlotScore1, blueSlotScore0) * step(0.01, blueSlotScore0);
    float blueSlot1 = (1.0 - blueSlot0) * step(0.01, blueSlotScore1);
    float visibleTravel0 = saturate(eventTravel0 / 0.94);
    float visibleTravel1 = saturate(eventTravel1 / 0.94);
    float fullTravelMap0 = visibleTravel0;
    float fullTravelMap1 = visibleTravel1;
    float eventHead0 = lerp(0.18, 1.00, fullTravelMap0);
    float eventHead1 = lerp(0.18, 1.00, fullTravelMap1);
    float fullBlue0 = step(1.28, eventStrength0) * blueSlot0;
    float fullBlue1 = step(1.28, eventStrength1) * blueSlot1;
    float exitBrightness0 = smoothstep(0.18, 0.56, eventHead0) * fullBlue0;
    float exitBrightness1 = smoothstep(0.18, 0.56, eventHead1) * fullBlue1;
    float fullBlueFadeIn0 = max(smoothstep(0.34, 0.78, eventTravel0), exitBrightness0);
    float fullBlueFadeIn1 = max(smoothstep(0.34, 0.78, eventTravel1), exitBrightness1);
    float brightBlueHead0 = smoothstep(0.30, 0.50, eventHead0) * fullBlue0;
    float brightBlueHead1 = smoothstep(0.30, 0.50, eventHead1) * fullBlue1;
    float fullBlueWidth0 = lerp(0.024, 0.013, exitBrightness0);
    float fullBlueWidth1 = lerp(0.024, 0.013, exitBrightness1);
    float fullBlueRing0 = exp(-pow((tunnelBand - eventHead0) / fullBlueWidth0, 2.0)) * fullBlueFadeIn0 * fullBlue0 * (0.42 + exitBrightness0 * 2.05);
    float fullBlueRing1 = exp(-pow((tunnelBand - eventHead1) / fullBlueWidth1, 2.0)) * fullBlueFadeIn1 * fullBlue1 * (0.42 + exitBrightness1 * 2.05);
    float fullBluePulseRaw = max(fullBlueRing0, fullBlueRing1);
    float fullBluePulse = fullBluePulseRaw;
    float eventPurpleLimit = 1.0 - smoothstep(0.76, 0.88, tunnelBand);
    float eventPurpleGate0 = lerp(purpleBlueBlock, eventPurpleLimit, strongEvent0);
    float eventPurpleGate1 = lerp(purpleBlueBlock, eventPurpleLimit, strongEvent1);
    float eventPulseRing = saturate(eventRing0 * eventPurpleGate0 + eventRing1 * eventPurpleGate1);
    float weakEventActivity = saturate(
      tunnelPackedPulseLife(eventTravel0) * eventStrength0 * (1.0 - strongEvent0) +
      tunnelPackedPulseLife(eventTravel1) * eventStrength1 * (1.0 - strongEvent1));
    float weakPurpleArea = purpleRegionLevel * weakEventActivity * saturate(0.12 + blueRingGate * 0.16);
    float eventEdgeTravel0 = smoothstep(0.54, 0.76, eventTravel0) * (1.0 - smoothstep(0.86, 0.99, eventTravel0));
    float eventEdgeTravel1 = smoothstep(0.54, 0.76, eventTravel1) * (1.0 - smoothstep(0.86, 0.99, eventTravel1));
    float edgeA = saturate(eventEdgeTravel0 * fullBlue0);
    float edgeB = saturate(eventEdgeTravel1 * fullBlue1);
    float edgeArrival = saturate(max(edgeA, edgeB) * 0.86);
    float strongOuterEvent = saturate(max(eventEdgeTravel0 * fullBlue0, eventEdgeTravel1 * fullBlue1));
    float blueEdgeBand = purplePulseMask;
    float blueEdgePulse = 0.0;
    float blueEdgeFlash = blueEdgeBand * edgeArrival * saturate(0.10 + heavyPulseGate * 0.18);
    float blueExitPower = saturate(max(edgeArrival, fullBluePulseRaw) * (0.70 + heavyPulseGate * 0.36));
    float blueExitValve = smoothstep(0.70, 0.84, blueExitPower);
    float blueOuterGate = lerp(1.0, blueExitValve, smoothstep(0.72, 0.86, tunnelBand));
    float eventBluePulse = fullBluePulse;
    float outerBlueBand = max(purplePulseMask, max(fullBlue0, fullBlue1));
    float narrowAperture0 = exp(-pow((tunnelBand - eventHead0) / (fullBlueWidth0 * 1.45), 2.0)) * fullBlue0;
    float narrowAperture1 = exp(-pow((tunnelBand - eventHead1) / (fullBlueWidth1 * 1.45), 2.0)) * fullBlue1;
    float outerBlueWash0 = outerBlueBand * blueExitValve * narrowAperture0 * smoothstep(0.36, 0.84, eventTravel0) * fullBlue0;
    float outerBlueWash1 = outerBlueBand * blueExitValve * narrowAperture1 * smoothstep(0.36, 0.84, eventTravel1) * fullBlue1;
    float brightBlueHead = max(brightBlueHead0, brightBlueHead1);
    float exitBrightness = max(exitBrightness0, exitBrightness1);
    float blueOuterWash = saturate(max(outerBlueWash0, outerBlueWash1) * (0.12 + heavyPulseGate * 0.10 + exitBrightness * 0.20));
    float blueExpandPulse = eventBluePulse * saturate(0.28 + heavyPulseGate * 0.12 + exitBrightness * 0.56);
    float bluePulse = saturate(blueEdgeFlash * 0.08 + blueExpandPulse * 0.72 + blueOuterWash + exitBrightness * eventBluePulse * 0.42);
    float breathBlueMix = saturate((blueEdgeFlash * 0.10 + blueExpandPulse + blueOuterWash * 0.40) * saturate(heavyPulseGate * 0.68 + strongOuterEvent * 0.58 + exitBrightness * 0.78));
    float3 breathPulseColor = lerp(float3(0.62, 0.34, 1.00), float3(0.30, 0.94, 1.00), breathBlueMix);
    float purpleBand = saturate(purpleRegionLevel * 0.56);
    float pixelBlueBand = saturate(bluePulse * 0.52);
    float purplePulse = saturate(purpleRegionLevel * (0.56 + ringBeat * 0.20) + bluePulse * 0.32);
    float pulseAllowedMask = saturate(purplePulseMask + brightPurpleBreath + bluePulse);
    float sweepLight = saturate(depthAlpha * 0.64 + pixelBlueBand * 0.42 + purplePulse * 0.38);
    float brightBlueRamp = saturate(smoothstep(0.34, 0.56, tunnelBand) * purpleRegionMask);
    float3 cyanBandColor = float3(0.28, 0.96, 1.10);
    float3 purpleRingColor = lerp(float3(0.94, 0.56, 1.28), float3(1.00, 0.66, 1.34), brightInkAdapt);
    float3 brightPurpleBreathColor = lerp(float3(1.04, 0.66, 1.34), float3(1.08, 0.72, 1.38), brightInkAdapt);
    float darkToBrightPurpleRamp = pow(saturate((tunnelBand + 0.035) / 0.24), 0.50);
    float brightPurpleToBlueRamp = smoothstep(0.36, 0.56, tunnelBand);
    float purpleTransitionMask = saturate(purpleRegionMask * (1.0 - smoothstep(0.54, 0.64, tunnelBand)));
    float3 saturatedDarkPurpleColor = lerp(float3(0.48, 0.16, 0.86), float3(0.64, 0.26, 1.04), brightInkAdapt);
    float3 brightPurpleMidColor = lerp(float3(1.00, 0.48, 1.34), float3(1.12, 0.62, 1.44), brightInkAdapt);
    float3 purpleStageColor = lerp(saturatedDarkPurpleColor, brightPurpleMidColor, darkToBrightPurpleRamp);
    float3 purpleTransitionColor = lerp(purpleStageColor, cyanBandColor * 0.92, brightPurpleToBlueRamp);
    float gradientBreathPhase = frac(uRuntime.z * 0.22 + 0.13);
    float gradientBreath = smoothstep(0.06, 0.94, 0.5 - 0.5 * cos(gradientBreathPhase * 6.2831853));
    float gradientBreathMask = saturate(purpleRegionMask * smoothstep(0.06, 0.24, tunnelBand) * (1.0 - smoothstep(0.42, 0.56, tunnelBand)));
    float gradientBreathRamp = saturate(brightBlueRamp + (gradientBreath - 0.5) * 0.055 * gradientBreathMask);
    float3 purpleBlueGradientColor = lerp(
      purpleRingColor * (0.97 + gradientBreath * 0.060),
      cyanBandColor * (0.95 + gradientBreath * 0.045),
      gradientBreathRamp);
    float3 weakPurpleColor = lerp(float3(0.66, 0.42, 1.16), float3(0.86, 0.52, 1.26), brightInkAdapt * 0.74);
    float purpleBgKeep = (1.0 - textureTint * lerp(0.18, 0.10, bgRead));
    float innerPurpleVisibility = purplePulseMask * (1.0 - smoothstep(0.50, 0.66, tunnelBand));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleTransitionColor * (0.58 + purpleRegionLevel * 0.26 + gradientBreath * 0.04)), saturate(purpleTransitionMask * (0.22 + purplePulseMask * 0.06)) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, brightPurpleBreathColor * (0.66 + brightPurpleBreath * 0.46)), saturate(brightPurpleBreath * 0.38) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleBlueGradientColor * (0.44 + gradientBreath * 0.12)), saturate(gradientBreathMask * (0.025 + gradientBreath * 0.045)) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleRingColor * (0.46 + purpleRegionLevel * 0.28)), saturate(innerPurpleVisibility * 0.24) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleBlueGradientColor * (0.52 + brightBlueRamp * 0.24)), saturate(purpleRegionMask * 0.30) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleBlueGradientColor * (0.50 + purpleRegionLevel * 0.66)), saturate(purpleRegionLevel * 0.28) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, breathPulseColor * (0.40 + purpleRegionLevel * 0.18 + bluePulse * 0.42)), saturate(purpleRegionLevel * 0.13 + bluePulse * 0.26) * (1.0 - textureTint * 0.42));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleBlueGradientColor * (0.76 + purpleRegionLevel * 0.70)), saturate(purpleRegionLevel * 0.30 + purplePulse * 0.16) * purpleBgKeep);
    tunnelColor = lerp(tunnelColor, max(tunnelColor, weakPurpleColor * (0.42 + weakPurpleArea * 0.46 + depthLayer * 0.08)), saturate(weakPurpleArea * 0.34) * (1.0 - textureTint * 0.44));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleBlueGradientColor * (0.52 + purpleBand * 0.40)), saturate(purpleBand * 0.28) * (1.0 - textureTint * 0.46));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, cyanBandColor * (0.46 + pixelBlueBand * 0.38 + brightBlueHead * 0.28)), saturate(pixelBlueBand * 0.20 + bluePulse * 0.12 + brightBlueHead * 0.10) * (1.0 - textureTint * 0.46));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, purpleRingColor * (0.36 + purplePulse * 0.30)), saturate(purplePulse * 0.20) * (1.0 - textureTint * 0.50));
    tunnelColor = lerp(tunnelColor, max(tunnelColor, uHighlight.rgb * (0.38 + sweepLight * 0.16)), saturate(purpleRegionLevel * 0.018 + bluePulse * 0.020) * (1.0 - textureTint * 0.62));
    float3 desktopDarkInk = lerp(
      lerp(float3(0.11, 0.23, 0.30), float3(0.28, 0.19, 0.48), tunnelBand),
      lerp(float3(0.34, 0.60, 1.00), float3(0.72, 0.42, 1.12), tunnelBand),
      brightInkAdapt);
    float tubeLineShade = (1.0 + purpleRegionLevel * 0.08 + brightPurpleBreath * 0.14 + bluePulse * 0.10);
    tunnelColor *= tubeLineShade;
    float3 inactiveInkCeiling = lerp(float3(0.30, 0.38, 0.54), float3(0.58, 0.82, 1.18), brightInkAdapt);
    tunnelColor = lerp(min(tunnelColor, inactiveInkCeiling), tunnelColor, pulseAllowedMask);
    float3 brightBgInactiveInk = lerp(float3(0.50, 0.78, 1.16), float3(0.92, 0.58, 1.24), smoothstep(0.18, 0.58, tunnelBand));
    tunnelColor = lerp(tunnelColor, brightBgInactiveInk * (0.84 + depthLayer * 0.18), brightInkAdapt * (1.0 - pulseAllowedMask) * 0.62);
    float3 desktopReadColor = lerp(desktopDarkInk, max(tunnelColor * 0.88, tunnelTheme * 0.76), 0.44 + depthLayer * 0.26);
    tunnelColor = lerp(tunnelColor, desktopReadColor, bgRead * 0.36);
    float keepBlack = (1.0 - smoothstep(0.025, 0.115, lumVal)) * textureTint;
    float textureContinuity = lerp(1.0, 0.90 + smoothstep(0.035, 0.36, lumVal) * 0.10, textureTint);
    float crispCore = saturate(dotAlpha * (0.90 + sphereCore * 0.16 - sphereEdge * 0.12));
    float inactiveAlphaBase = lerp(0.39 + depthLayer * 0.26, 0.34 + depthLayer * 0.22, brightInkAdapt);
    float baseAlphaBody = lerp(inactiveAlphaBase, 0.42 + depthLayer * 0.43, pulseAllowedMask);
    float alphaMain = saturate(crispCore * uOpacity * max(0.12, uVisualA.x) * depthAlpha * textureContinuity * (baseAlphaBody + pixelBlueBand * 0.08 * purplePulseMask + purpleRegionLevel * 0.16 + brightPurpleBreath * 0.09 + bluePulse * 0.12) * lerp(1.0, 0.97, bgRead));
    float bloomKeep = 1.0 - keepBlack * 0.92;
    float bloomDesktopGuard = lerp(1.0, 0.34, bgRead);
    float baseBloomBody = lerp(0.006 + depthLayer * 0.010, 0.014 + depthLayer * 0.026, pulseAllowedMask);
    float alphaBloom = saturate(dotSoft * uOpacity * max(0.0, uVisualB.w) * max(0.12, uVisualA.x) * depthAlpha * textureContinuity * (baseBloomBody + pixelBlueBand * 0.024 * purplePulseMask + purpleRegionLevel * 0.034 + brightPurpleBreath * 0.028 + bluePulse * 0.030) * 0.034 * bloomKeep * bloomDesktopGuard);
    float alpha = lerp(alphaMain, alphaBloom, bloomPass);
    float inactiveShadeBase = lerp(0.33 + depthLayer * 0.24, 0.48 + depthLayer * 0.28, brightInkAdapt);
    float baseShade = lerp(inactiveShadeBase, 0.44 + depthLayer * 0.50, pulseAllowedMask);
    float shade = baseShade + pixelBlueBand * 0.12 * purplePulseMask + purpleRegionLevel * 0.24 + brightPurpleBreath * 0.18 + bluePulse * 0.22;
    float outLum = dot(tunnelColor * shade, float3(0.299, 0.587, 0.114));
    float lightParticle = smoothstep(0.50, 0.82, outLum) * (1.0 - keepBlack);
    float darkParticle = (1.0 - smoothstep(0.20, 0.50, outLum)) * (1.0 - keepBlack);
    float3 lit = tunnelColor * shade;
    lit = lerp(lit, float3(0.0, 0.0, 0.0), readableRim * lightParticle * 0.24);
    lit = lerp(lit, float3(1.0, 1.0, 1.0), readableRim * darkParticle * 0.08);
    float3 bloomLit = tunnelColor * (0.018 + shade * 0.032 + pixelBlueBand * 0.036 * purplePulseMask + purpleRegionLevel * 0.020 + brightPurpleBreath * 0.030 + bluePulse * 0.026) + purpleRingColor * saturate(purpleRegionLevel * 0.010 + brightPurpleBreath * 0.024 + bluePulse * 0.016) * (1.0 - textureTint * 0.58);
    bloomLit = clamp(bloomLit, float3(0.0, 0.0, 0.0), float3(1.45, 1.45, 1.45));
    lit = lerp(lit, bloomLit, bloomPass);
    lit *= (0.98 + depthNear * 0.08 - depthFar * 0.08);
    return float4(lit * alpha, alpha);
  }
  if (preset == 2.0 && input.color.a > 0.0) {
    float depthAlpha = saturate(input.color.a);
    float depthLayer = saturate(input.depthInfo);
    float glow = saturate(input.aux);
    float equatorBand = saturate(input.color.g);
    float shockBand = saturate(input.color.b);
    float lonRaw = saturate(1.0 - abs(frac(input.uv.x * 24.0) - 0.5) * 2.0);
    float latRaw = saturate(1.0 - abs(frac(input.uv.y * 12.0) - 0.5) * 2.0);
    float lonMajor = pow(saturate(1.0 - abs(frac(input.uv.x * 8.0) - 0.5) * 2.0), 7.8);
    float latMajor = pow(saturate(1.0 - abs(frac(input.uv.y * 6.0) - 0.5) * 2.0), 7.4);
    float lonLine = pow(lonRaw, 18.0);
    float latLine = pow(latRaw, 17.2);
    float majorLine = saturate(max(lonMajor, latMajor));
    float fineLine = max(lonLine * 0.72, latLine * 0.68);
    float majorCore = saturate(majorLine * 1.65);
    float orbitLineMask = saturate(fineLine * 0.22 + majorCore * 3.85 + equatorBand * 5.20 + shockBand * 6.35);
    float gridLine = smoothstep(0.48, 0.86, orbitLineMask);
    float structureLine = saturate(gridLine * 0.78 + fineLine * 0.05 + majorCore * 3.45 + equatorBand * 4.28 + shockBand * 5.34);
    float3 sampled = sampleCoverTile(input.uv, input.local, 1.0 / 167.0, 0.62, bloomPass).rgb;
    sampled = pow(max(sampled, float3(0.0, 0.0, 0.0)), float3(0.88, 0.88, 0.88));
    float3 orbitCool = lerp(float3(0.18, 0.78, 1.00), float3(0.76, 0.48, 1.00), saturate(input.uv.y * 0.64 + input.color.r * 0.22));
    float orbitTextureTint = saturate(uUseTexture);
    float bgConfidence = saturate(uBackground.w);
    float bgLuma = saturate(uBackground.x);
    float contrastBg = saturate(uBackground.y) * bgConfidence;
    float brightBg = smoothstep(0.46, 0.86, bgLuma) * bgConfidence;
    float darkBg = (1.0 - smoothstep(0.10, 0.34, bgLuma)) * bgConfidence;
    float midBg = smoothstep(0.30, 0.58, bgLuma) * (1.0 - brightBg * 0.55) * bgConfidence;
    float busyBg = smoothstep(0.24, 0.66, max(contrastBg, saturate(uBackground.z))) * bgConfidence;
    float lowContrastBg = (1.0 - smoothstep(0.16, 0.50, contrastBg)) * smoothstep(0.22, 0.72, bgLuma) * bgConfidence;
    float bgRead = saturate(max(max(brightBg, busyBg * 0.72), max(midBg * 0.62, lowContrastBg * 0.52)));
    float orbitFront = smoothstep(0.24, 0.92, depthLayer);
    float orbitBack = 1.0 - orbitFront;
    float sampledMax = max(max(sampled.r, sampled.g), sampled.b);
    float brightCoverGuard = smoothstep(0.46, 0.82, sampledMax) * orbitTextureTint;
    float3 sampledOrbit = sampled * (0.74 + gridLine * 0.020 + majorCore * 0.050 + equatorBand * 0.038 + shockBand * 0.054);
    float3 coverColor = sampledOrbit;
    float3 orbitBase = lerp(orbitCool, coverColor, orbitTextureTint * 0.60);
    orbitBase = lerp(orbitBase, orbitCool * 0.86 + uSecondary.rgb * 0.14, brightCoverGuard * 0.64);
    float desktopCoverGuard = saturate((midBg * 0.62 + bgRead * 0.26) * (0.35 + brightCoverGuard * 0.65));
    orbitBase = lerp(orbitBase, orbitCool * (0.76 + orbitFront * 0.10) + uSecondary.rgb * 0.08, desktopCoverGuard);
    orbitBase = lerp(orbitBase, lerp(float3(0.035, 0.13, 0.24), float3(0.08, 0.24, 0.46), saturate(equatorBand + shockBand)), bgRead * 0.56);
    float3 brightOrbitInk = lerp(float3(0.012, 0.080, 0.155), float3(0.030, 0.205, 0.390), saturate(equatorBand + shockBand + orbitFront * 0.32));
    orbitBase = lerp(orbitBase, brightOrbitInk, bgRead * (0.54 + orbitBack * 0.22));
    float3 midOrbitInk = lerp(float3(0.018, 0.105, 0.200), float3(0.040, 0.280, 0.500), saturate(equatorBand + shockBand + orbitFront * 0.26));
    orbitBase = lerp(orbitBase, midOrbitInk, midBg * 0.34 * (0.55 + orbitBack * 0.28));
    float3 quietOrbitInk = lerp(float3(0.020, 0.115, 0.210), float3(0.060, 0.320, 0.560), saturate(equatorBand + shockBand + orbitFront * 0.22));
    orbitBase = lerp(orbitBase, quietOrbitInk, lowContrastBg * 0.42);
    orbitBase = lerp(orbitBase, uSecondary.rgb, 0.045 * (1.0 - orbitTextureTint * 0.86));
    float3 orbitWarm = lerp(orbitBase * 1.08, float3(1.00, 0.88, 0.54), 0.22);
    orbitBase = lerp(orbitBase, orbitWarm, saturate(equatorBand * 0.13 + shockBand * 0.18) * (1.0 - orbitTextureTint * 0.90));
    orbitBase = lerp(orbitBase, max(orbitBase, float3(0.72, 0.86, 1.00) * 0.20), saturate((lonMajor + latMajor) * 0.0010 + glow * 0.0008 + pulse * 0.0003) * (1.0 - orbitTextureTint * 0.96));
    orbitBase = lerp(orbitBase, coverColor, orbitTextureTint * 0.60);
    float leadPacked = max(0.0, input.beat);
    float leadBoost = saturate(frac(leadPacked));
    float leadColorId = saturate(floor(leadPacked + 0.001) / 4.0);
    float leadColorMix = saturate(glow * 1.18) * (0.30 + leadBoost * 0.18);
    float3 leadPaletteA = lerp(float3(0.20, 0.82, 1.22), float3(0.86, 0.52, 1.20), smoothstep(0.00, 0.25, leadColorId));
    float3 leadPaletteB = lerp(float3(0.38, 1.05, 0.84), float3(1.05, 0.82, 0.34), smoothstep(0.25, 0.75, leadColorId));
    float3 leadPaletteC = lerp(leadPaletteB, float3(1.04, 0.45, 0.62), smoothstep(0.75, 1.00, leadColorId));
    float3 randomLeadColor = lerp(leadPaletteA, leadPaletteC, smoothstep(0.25, 0.62, leadColorId));
    float3 leadHeadColor = max(coverColor * 1.10, randomLeadColor);
    orbitBase = lerp(orbitBase, leadHeadColor, leadColorMix);
    float orbitCore = saturate(dotAlpha * (0.98 + sphereCore * 0.040) - sphereEdge * 0.48);
    float shellAlpha = 0.0;
    float frontLine = 0.010 + pow(depthLayer, 1.22) * 0.99;
    float lineDepthGain = lerp(0.46, 1.10, orbitFront);
    float lineAlpha = (fineLine * (0.07 + depthLayer * 0.08) + majorCore * 2.34 + equatorBand * 2.92 + shockBand * 3.82) * lineDepthGain * lerp(1.0 + darkBg * 0.08, 1.18, bgRead);
    float alphaMain = saturate(depthAlpha * uOpacity * max(0.12, uVisualA.x) * orbitCore * (shellAlpha + lineAlpha + pulse * 0.0006) * materialDepthSolid * structureLine * lerp(1.0 + darkBg * 0.10, 1.10, bgRead));
    alphaMain *= frontLine;
    float alphaBloom = saturate(depthAlpha * uOpacity * max(0.12, uVisualA.x) * outerGlowAlpha * (shockBand * 0.000014 + equatorBand * 0.000008 + majorCore * 0.000005 + glow * 0.0000003) * (0.00024 + depthLayer * 0.00034) * materialDepthGlow * lerp(1.0, 0.46, bgRead));
    float alpha = lerp(alphaMain, alphaBloom, bloomPass);
    float shade = 0.0010 + sphereLight * (0.00045 + depthLayer * 0.00034) + sphereCore * 0.0006 - sphereEdge * 0.52 + gridLine * 0.36 + majorCore * 1.62 + equatorBand * 2.08 + shockBand * 2.95;
    float3 lit = orbitBase * shade + lerp(uHighlight.rgb, orbitBase, orbitTextureTint) * sphereSpec * (0.00007 + shockBand * 0.00022 + equatorBand * 0.00012);
    float litLum = dot(lit, float3(0.299, 0.587, 0.114));
    float3 orbitHue = orbitCool * (0.82 + orbitFront * 0.10) + uSecondary.rgb * 0.06;
    lit = lerp(lit, orbitHue * litLum * 0.92, saturate(midBg * 1.12 + brightCoverGuard * 0.18));
    float3 bloomLit = orbitBase * (0.00005 + shockBand * 0.00016 + equatorBand * 0.00007 + majorCore * 0.000024) + lerp(uHighlight.rgb, orbitBase, orbitTextureTint) * (0.000002 + shockBand * 0.000009);
    lit = lerp(lit, bloomLit, bloomPass);
    lit *= (0.002 + depthLayer * 0.012 + input.color.r * 0.00035 + gridLine * 0.34 + majorCore * 1.02 + equatorBand * 1.06 + shockBand * 1.30) * (0.42 + depthNear * 0.38 - depthFar * 0.46);
    float orbitCeil = lerp(1.18, 0.98, midBg * 0.34);
    lit = min(lit, float3(orbitCeil, orbitCeil, orbitCeil));
    // On bright wallpapers the player-like white highlights turn into a pale
    // shell. Keep the line structure but pull the final ink back toward the
    // preset's blue/cyan orbit color only when the background read is bright.
    float brightInkMix = brightBg * lerp(0.36, 0.58, orbitBack);
    float3 brightLineInk = brightOrbitInk * (0.72 + gridLine * 0.24 + equatorBand * 0.28 + shockBand * 0.34 + orbitFront * 0.10);
    lit = lerp(lit, brightLineInk, brightInkMix);
    return float4(lit * alpha, alpha);
  }
  if (preset == 3.0) {
    float depthLayer = saturate(input.depthInfo);
    float dustSeed = hash(input.rnd * 991.0 + floor(input.uv.y * 160.0));
    float dust = smoothstep(0.982, 0.999, dustSeed);
    float rippleBright = saturate(input.aux * 1.5);
    float voidDot = saturate(dotAlpha * 0.88 + sphereCore * 0.10 - sphereEdge * 0.18);
    float bgConfidence = saturate(uBackground.w);
    float bgLuma = saturate(uBackground.x);
    float contrastBg = saturate(uBackground.y) * bgConfidence;
    float brightBg = smoothstep(0.54, 0.86, bgLuma) * bgConfidence;
    float darkBg = (1.0 - smoothstep(0.08, 0.32, bgLuma)) * bgConfidence;
    float busyBg = smoothstep(0.24, 0.58, max(contrastBg, saturate(uBackground.z))) * bgConfidence;
    float lowContrastBg = (1.0 - smoothstep(0.14, 0.46, contrastBg)) * smoothstep(0.22, 0.76, bgLuma) * bgConfidence;
    float voidBgRead = saturate(max(max(brightBg, busyBg * 0.86), lowContrastBg * 0.68));
    float voidBodyGain = lerp(1.0 + darkBg * 0.12, 1.62, voidBgRead);
    float voidBloomGain = lerp(1.0 + darkBg * 0.08, 0.42, voidBgRead);
    float voidSubPx = max(uAudioA.x * 0.34, uBass * 0.06);
    float voidAirPx = max(uAudioB.w, uTreble * 0.06);
    float voidReadableDust = smoothstep(0.968, 0.999, dustSeed) * voidBgRead;
    float voidParticleAlpha = max(input.color.a, voidReadableDust * 0.20);
    float readableVoidAlpha = dotAlpha * voidReadableDust * (0.030 + depthLayer * 0.026);
    float alphaMain = saturate(voidParticleAlpha * uOpacity * max(0.10, uVisualA.x) * voidDot * (1.48 + depthLayer * 0.40 + dustSeed * 0.18 + rippleBright * 0.16) * materialDepthSolid * voidBodyGain + readableVoidAlpha * uOpacity * max(0.10, uVisualA.x));
    float alphaBloom = saturate(input.color.a * uOpacity * max(0.0, uVisualB.w) * max(0.10, uVisualA.x) * dotSoft * dust * (0.0014 + voidAirPx * 0.0022 + pulse * 0.00050 + rippleBright * 0.0026) * materialDepthGlow * voidBloomGain);
    float alpha = lerp(alphaMain, alphaBloom, bloomPass);
    float playerBright = 1.18 + rippleBright * 0.30 + voidAirPx * 0.022 + voidSubPx * 0.010;
    float3 voidTile = sampleCoverTile(input.uv, input.local, 1.0 / 167.0, 0.34, bloomPass).rgb;
    voidTile = pow(max(voidTile, float3(0.0, 0.0, 0.0)), float3(0.92, 0.92, 0.92));
    float voidTileLum = dot(voidTile, float3(0.299, 0.587, 0.114));
    float voidTileMax = max(max(voidTile.r, voidTile.g), voidTile.b);
    float voidTileMin = min(min(voidTile.r, voidTile.g), voidTile.b);
    float voidTileChroma = voidTileMax - voidTileMin;
    float3 voidTextureColor = input.color.rgb * (0.72 + voidTileLum * 0.48 + voidTileChroma * 0.16) + voidTile * 0.18;
    float3 baseColor = lerp(input.color.rgb, voidTextureColor, saturate(uUseTexture) * 0.26) * playerBright;
    float3 voidDesktopInk = lerp(float3(0.020, 0.045, 0.085), float3(0.13, 0.22, 0.36), saturate(rippleBright + depthLayer * 0.36 + voidAirPx * 0.10));
    baseColor = lerp(baseColor, voidDesktopInk * (0.72 + rippleBright * 0.18), voidBgRead * 0.68);
    float3 bloomColor = baseColor * (0.060 + air * 0.018 + rippleBright * 0.018) * lerp(1.0, 0.42, voidBgRead) + uHighlight.rgb * 0.00035 * lerp(1.0, 0.32, voidBgRead);
    float3 lit = lerp(baseColor, bloomColor, bloomPass);
    lit *= 0.92 + depthNear * 0.10 - depthFar * 0.12;
    return float4(lit * alpha, alpha);
  }
  if (preset == 4.0 && input.color.a > 0.0) {
    float depthLayer = saturate(input.depthInfo);
    float coverMask = saturate(input.color.g);
    float ringMask = saturate(input.color.b);
    float recordAlpha = saturate(input.color.a);
    float2 tileD = input.local * 2.0 - 1.0;
    float tileSquare = max(abs(tileD.x), abs(tileD.y));
    float tileTop = 1.0 - smoothstep(0.64, 1.0, tileSquare);
    float tileEdge = smoothstep(0.54, 0.98, tileSquare);
    float tileSide = saturate(tileEdge * (0.55 + input.aux * 0.70));
    float2 recordPlane = (input.uv - 0.5) * 5.12;
    float angle0 = atan2(recordPlane.y, recordPlane.x);
    float recordR = 2.46;
    float coverR = 1.18;
    float disc = length(recordPlane);
    float hiResGuard = smoothstep(1.08, 1.55, max(0.1, uRuntime.w));
    float edgeGuard = lerp(1.0, 0.38, hiResGuard);
    float grooveGuard = lerp(1.0, 0.48, hiResGuard);
    float2 coverUv = recordPlane / (coverR * 2.0) + 0.5;
    float vinylN = saturate((disc - coverR) / max(0.001, recordR - coverR));
    float border = exp(-pow((disc - coverR) / 0.064, 2.0)) * edgeGuard;
    float outerRim = exp(-pow((disc - (recordR - 0.050)) / 0.055, 2.0)) * edgeGuard;
    float whiteRing = max(border * 0.92, outerRim * 0.26);
    float groove = 0.5 + 0.5 * sin((disc - coverR) * lerp(98.0, 58.0, hiResGuard));
    float fineGroove = 0.5 + 0.5 * sin((disc - coverR) * lerp(170.0, 92.0, hiResGuard) + input.rnd * 3.0);
    float grooveStripe = (groove - 0.5) * (1.0 - coverMask);
    float fineStripe = (fineGroove - 0.5) * (1.0 - coverMask);
    float grooveContrast = saturate(0.46 + grooveStripe * 0.92 * grooveGuard + fineStripe * 0.50 * grooveGuard + whiteRing * 0.72);
    float tick = smoothstep(0.82, 0.995, hash(floor((angle0 + 3.1415926) * 38.0) + floor(disc * 72.0) * 2.1));
    float sweep = pow(0.5 + 0.5 * cos(angle0 - uTime * (1.08 + midFlow * 0.30)), 18.0) * kick;
    float discN = saturate(disc / recordR);
    float radialRipple = max(0.0, sin(disc * 7.4 - uTime * (2.15 + midFlow * 0.72) + kick * 1.7));
    radialRipple *= recordAlpha * (0.026 + midFlow * 0.075 + kick * 0.048) * max(0.1, uVisualA.x);
    float pillarRing = floor(discN * 24.0);
    float pillarArc = floor((angle0 + 3.1415926) / 6.2831852 * (44.0 + pillarRing * 0.32));
    float pillarSeed = hash(pillarRing * 37.17 + pillarArc * 11.13);
    float pillarCell = smoothstep(0.26, 0.96, pillarSeed);
    float pillarGlow = saturate(pillarCell * (0.18 + input.aux * 0.82) * (1.0 - smoothstep(recordR - 0.24, recordR + 0.01, disc)));
    float meteorSeed = hash(floor(input.uv.y * 84.0) * 17.7 + floor(input.uv.x * 13.0) * 3.1);
    float meteorLane = smoothstep(0.948, 0.998, meteorSeed) * recordAlpha;
    float meteorPhase = frac(input.uv.x + uTime * (0.24 + spark * 0.16 + presence * 0.050) + meteorSeed * 0.71);
    float meteorCore = exp(-pow((meteorPhase - 0.58) / 0.105, 2.0));
    float2 meteorDir = normalize(float2(0.88, -0.48));
    float2 meteorPerp = float2(-meteorDir.y, meteorDir.x);
    float2 meteorPlane = meteorDir * ((meteorPhase - 0.5) * 5.6) + meteorPerp * ((meteorSeed - 0.5) * 3.7);
    float meteorInside = 1.0 - smoothstep(recordR * 0.92, recordR * 1.12, length(meteorPlane));
    float meteorGlow = saturate(meteorLane * meteorCore * meteorInside * smoothstep(0.05, 0.78, presence + spark * 0.48) * 1.25);
    float3 coverSample = sampleCoverTile(saturate(coverUv), input.local, 1.0 / 128.0, 0.68, bloomPass).rgb;
    float3 themeCover = lerp(uPrimary.rgb, uSecondary.rgb, saturate(coverUv.y));
    coverSample = lerp(themeCover, coverSample, saturate(uUseTexture));
    float bgConfidence = saturate(uBackground.w);
    float bgLuma = saturate(uBackground.x);
    float contrastBg = saturate(uBackground.y) * bgConfidence;
    float brightBg = smoothstep(0.46, 0.86, bgLuma) * bgConfidence;
    float darkBg = (1.0 - smoothstep(0.08, 0.28, bgLuma)) * bgConfidence;
    float busyBg = smoothstep(0.24, 0.66, max(contrastBg, saturate(uBackground.z))) * bgConfidence;
    float lowContrastBg = (1.0 - smoothstep(0.15, 0.48, contrastBg)) * smoothstep(0.22, 0.72, bgLuma) * bgConfidence;
    float bgRead = saturate(max(max(brightBg, busyBg * 0.58), lowContrastBg * 0.46));
    float coverEdge = exp(-pow((disc - coverR) / 0.048, 2.0)) * edgeGuard;
    whiteRing = max(max(coverEdge * 1.02, border * 0.76), outerRim * 0.34);
    float3 vinyl = float3(0.030, 0.032, 0.038) + float3(0.046, 0.047, 0.052) * groove * grooveGuard + float3(0.020, 0.020, 0.024) * fineGroove * grooveGuard;
    vinyl = lerp(vinyl, coverSample * 0.30, 0.18 * (1.0 - vinylN));
    vinyl *= lerp(0.42, 1.48, grooveContrast);
    float coverRead = 1.18 + 0.22 * (1.0 - smoothstep(0.0, coverR, disc));
    float coverDesktopGuard = lerp(0.96, 0.58, bgRead) * lerp(1.0, 0.84, busyBg);
    float3 coverLit = coverSample * coverRead * coverDesktopGuard + themeCover * (0.022 * bgRead * coverMask);
    float3 baseColor = lerp(vinyl, coverLit, coverMask);
    float vinylLane = 1.0 - coverMask;
    baseColor = lerp(baseColor, lerp(float3(0.020, 0.026, 0.038), float3(0.075, 0.092, 0.120), grooveContrast), brightBg * vinylLane * 0.48);
    baseColor = lerp(baseColor, max(baseColor, float3(0.16, 0.18, 0.21)), darkBg * vinylLane * 0.52);
    baseColor = lerp(baseColor, lerp(float3(0.038, 0.052, 0.074), float3(0.12, 0.15, 0.18), grooveContrast), lowContrastBg * vinylLane * 0.34);
    baseColor = lerp(baseColor, coverSample * (0.42 + coverRead * 0.10) + themeCover * 0.030, brightBg * coverMask * 0.18);
    float3 pillarTint = lerp(float3(0.28, 0.74, 1.0), float3(1.0, 0.56, 0.82), pillarSeed);
    baseColor = lerp(baseColor, pillarTint, saturate(pillarGlow * 0.50 + radialRipple * 1.9));
    baseColor = lerp(baseColor, float3(0.96, 0.97, 0.96), saturate(max(ringMask, whiteRing)) * (0.94 + coverMask * 0.06));
    baseColor = lerp(baseColor, float3(1.0, 1.0, 1.0), saturate((tick * (presence + spark * 0.48) * (0.10 + border * 0.16) + sweep * outerRim * 0.86) * grooveGuard));
    baseColor = lerp(baseColor, float3(0.72, 0.94, 1.0) + float3(spark * 0.18, presence * 0.10, 0.20), saturate(meteorGlow));
    float recordCore = saturate(dotAlpha * (0.92 + tileTop * 0.16) - tileEdge * 0.030);
    float grooveRead = saturate(abs(grooveStripe) * 0.58 * grooveGuard + abs(fineStripe) * 0.30 * grooveGuard + whiteRing * 1.04 + sweep * outerRim * 0.70);
    float coverAlphaTerm = coverMask * lerp(0.48, 0.28, bgRead);
    float desktopVinylRead = 1.0 + vinylLane * (darkBg * 0.28 + brightBg * 0.14 + busyBg * 0.08 + lowContrastBg * 0.12);
    float alphaMain = saturate(max(recordAlpha, meteorGlow) * uOpacity * max(0.12, uVisualA.x) * recordCore * (0.42 + coverAlphaTerm + depthLayer * 0.16 + grooveRead * 1.10 + pillarGlow * 0.36 + radialRipple * 0.94 + meteorGlow * 0.42) * materialDepthSolid * lerp(1.0 + darkBg * 0.22, 0.96, bgRead) * desktopVinylRead);
    float alphaBloom = saturate(max(recordAlpha, meteorGlow) * uOpacity * max(0.12, uVisualA.x) * outerGlowAlpha * (0.0011 + whiteRing * 0.0072 + sweep * outerRim * 0.0140 + tick * presence * 0.0022 + pillarGlow * 0.0052 + radialRipple * 0.0100 + meteorGlow * 0.0200) * materialDepthGlow * lerp(1.0, 0.48, bgRead));
    float alpha = lerp(alphaMain, alphaBloom, bloomPass);
    float flatShade = 0.32 + depthLayer * 0.20 + coverMask * lerp(0.34, 0.18, bgRead) + grooveContrast * vinylLane * 0.82 + whiteRing * 0.94 + sweep * outerRim * 0.52 + pillarGlow * 0.46 + radialRipple * 0.70 + meteorGlow * 0.62 + darkBg * vinylLane * 0.18;
    baseColor = lerp(baseColor * (0.62 + depthLayer * 0.24), baseColor * (1.12 + pillarGlow * 0.72 + radialRipple * 0.42), tileTop);
    baseColor = lerp(baseColor, baseColor * float3(0.38, 0.48, 0.62), tileSide * vinylLane * (0.28 + depthLayer * 0.30));
    float3 lit = baseColor * flatShade + uHighlight.rgb * sphereSpec * (0.006 + whiteRing * 0.011 + sweep * 0.006);
    float3 bloomLit = baseColor * (0.012 + whiteRing * 0.020 + sweep * outerRim * 0.018) + uHighlight.rgb * (0.00045 + sweep * outerRim * 0.0030);
    lit = lerp(lit, bloomLit, bloomPass);
    lit *= 0.86 + depthNear * 0.06 - depthFar * 0.08;
    return float4(lit * alpha, alpha);
  }
  if (preset == 4.0) discard;
  if ((preset == 5.0 || preset == 6.0 || preset == 7.0) && input.color.a > 0.0) {
    float alphaCore = input.color.a * uOpacity * max(0.12, uVisualA.x) * dotAlpha;
    float bloom = uVisualC.z * max(0.0, uVisualB.w);
    float glow = saturate(input.aux);
    if (preset == 5.0) {
      float particleDim = max(0.12, uVisualA.x);
      float bgKnown = saturate(uBackground.w);
      float bgLuma = lerp(0.34, saturate(uBackground.x), bgKnown);
      float bgConfidence = max(0.35, bgKnown);
      float brightBg = smoothstep(0.46, 0.86, bgLuma) * bgConfidence;
      float darkBg = (1.0 - smoothstep(0.18, 0.42, bgLuma)) * bgConfidence;
      float midBg = smoothstep(0.22, 0.52, bgLuma) * (1.0 - brightBg * 0.55) * bgConfidence;
      float contrastBg = saturate(uBackground.y) * bgConfidence;
      float busyBg = smoothstep(0.24, 0.66, max(contrastBg, saturate(uBackground.z))) * bgConfidence;
      float lowContrastBg = (1.0 - smoothstep(0.14, 0.48, contrastBg)) * smoothstep(0.20, 0.72, bgLuma) * bgConfidence;
      float bgRead = saturate(max(max(brightBg, busyBg * 0.82), max(midBg * 0.62, lowContrastBg * 0.54)));
      float lane = saturate(input.uv.y);
      float dustLane = step(0.80, lane);
      float ribbonLane = 1.0 - dustLane;
      float coreGain = lerp(0.92, 1.08, darkBg) * lerp(1.0, 0.74, brightBg) * lerp(0.96, 0.24, dustLane);
      float bloomGain = lerp(0.90, 0.48, brightBg) * lerp(1.0, 0.18, dustLane) * lerp(0.92, 1.0, darkBg);
      float brightGain = lerp(1.02, 0.92, brightBg) * lerp(1.0, 0.84, busyBg);
      float beatGlowPx = saturate(input.beat);
      float vBright = 0.70 + glow * 0.12 + beatGlowPx * (0.10 * ribbonLane + 0.035 * dustLane);
      float galaxyBall = pow(saturate(1.0 - radial * 0.88), lerp(1.55, 1.85, dustLane));
      float galaxyCore = exp(-r2 * lerp(5.8, 7.8, dustLane));
      float galaxySpec = pow(saturate(dot(sphereN, normalize(float3(-0.42, 0.55, 0.72)))), lerp(3.6, 4.8, dustLane));
      float galaxyAlphaShape = saturate((galaxyBall * lerp(0.88, 0.70, dustLane) + galaxyCore * 0.24) * dotAlpha);
      float galaxyTileStrength = lerp(0.42, 0.20, dustLane);
      float3 galaxyTile = sampleCoverTile(input.uv, input.local, 1.0 / 167.0, galaxyTileStrength, bloomPass).rgb;
      galaxyTile = pow(max(galaxyTile, float3(0.0, 0.0, 0.0)), float3(0.92, 0.92, 0.92));
      float galaxyTileLum = dot(galaxyTile, float3(0.299, 0.587, 0.114));
      float galaxyTileMax = max(max(galaxyTile.r, galaxyTile.g), galaxyTile.b);
      float galaxyTileMin = min(min(galaxyTile.r, galaxyTile.g), galaxyTile.b);
      float galaxyTileChroma = galaxyTileMax - galaxyTileMin;
      float textureTint = saturate(uUseTexture);
      float textureMix = textureTint * lerp(0.32, 0.14, dustLane);
      float textureLight = lerp(1.0, 0.76 + galaxyTileLum * 0.54 + galaxyTileChroma * 0.16, textureTint * lerp(0.58, 0.26, dustLane));
      float3 galaxyTextureColor = input.color.rgb * (0.76 + galaxyTileLum * 0.30) + galaxyTile * (0.24 + galaxyTileChroma * 0.16);
      float3 galaxySource = lerp(input.color.rgb, galaxyTextureColor, textureMix);
      float3 particleBeatTint = lerp(float3(0.36, 0.82, 1.18), float3(0.92, 0.44, 1.30), saturate(input.color.r * 0.42 + lane * 0.58));
      float sourceLum = dot(max(galaxySource, float3(0.0, 0.0, 0.0)), float3(0.299, 0.587, 0.114));
      float keepBlack = 1.0 - smoothstep(0.025, 0.115, sourceLum);
      float nonBlack = 1.0 - keepBlack;
      float beatColorMix = beatGlowPx * nonBlack * (0.42 * ribbonLane + 0.18 * dustLane);
      galaxySource = lerp(galaxySource, particleBeatTint * (0.78 + sourceLum * 0.46), beatColorMix);
      float3 baseLit = galaxySource * vBright * brightGain * textureLight;
      float outLum = dot(baseLit, float3(0.299, 0.587, 0.114));
      float lightParticle = smoothstep(0.50, 0.82, outLum) * nonBlack;
      float darkParticle = (1.0 - smoothstep(0.20, 0.50, outLum)) * nonBlack;
      baseLit = baseLit * (1.02 + galaxySpec * lerp(0.42, 0.24, dustLane));
      baseLit += particleBeatTint * beatGlowPx * galaxyCore * nonBlack * (0.58 * ribbonLane + 0.20 * dustLane);
      baseLit = lerp(baseLit, max(baseLit, float3(0.74, 0.90, 1.0) * (0.50 + galaxySpec * 0.26)), brightBg * lerp(0.28, 0.16, dustLane));
      baseLit = lerp(baseLit, float3(0.0, 0.0, 0.0), readableRim * lightParticle * 0.08);
      baseLit = lerp(baseLit, float3(0.64, 0.84, 1.0), galaxyCore * darkParticle * 0.10 * ribbonLane);
      float3 ribbonContrast = lerp(float3(0.045, 0.11, 0.23), float3(0.15, 0.28, 0.52), saturate(glow + input.color.b * 0.48));
      float3 dustContrast = lerp(float3(0.050, 0.09, 0.18), float3(0.10, 0.16, 0.34), saturate(glow * 0.60 + input.color.b * 0.32));
      float3 brightContrast = lerp(ribbonContrast, dustContrast, dustLane);
      float desktopInkMix = saturate(brightBg * lerp(0.70, 0.52, dustLane) + midBg * lerp(0.62, 0.36, dustLane) + lowContrastBg * lerp(0.44, 0.28, dustLane));
      baseLit = lerp(baseLit, brightContrast, desktopInkMix);
      baseLit = lerp(baseLit, max(baseLit, float3(0.30, 0.50, 0.78) * 0.34), readableRim * brightBg * 0.05 * ribbonLane);
      baseLit = clamp(baseLit, float3(0.0, 0.0, 0.0), float3(1.6, 1.6, 1.6));
      float alphaLaneGain = lerp(0.96, 0.24, dustLane);
      float alphaBaseFlow = saturate(input.color.a * uOpacity * particleDim * galaxyAlphaShape * coreGain * alphaLaneGain * 0.98 * (1.0 + beatGlowPx * 0.14 * ribbonLane));
      float bloomKeep = 1.0 - keepBlack * 0.92;
      float bloomPulse = 1.0 + saturate(glow * 1.0 + beatGlowPx * 1.55) * 0.52;
      float3 bloomLit = clamp(galaxySource * (0.44 + vBright * 0.48) * lerp(1.0, 0.62, bgRead) + particleBeatTint * beatGlowPx * (0.66 * ribbonLane + 0.20 * dustLane), float3(0.0, 0.0, 0.0), float3(1.4, 1.4, 1.4));
      float brightAlphaSuppress = lerp(lerp(1.0, 0.72, brightBg), lerp(1.0, 0.45, brightBg), dustLane);
      float brightBloomSuppress = lerp(lerp(1.0, 0.48, brightBg), lerp(1.0, 0.25, brightBg), dustLane);
      alphaBaseFlow *= lerp(1.0, 1.45, midBg * ribbonLane) * brightAlphaSuppress;
      float alphaBloomFlow = saturate(input.color.a * uOpacity * bloom * particleDim * galaxyCore * bloomPulse * 0.082 * bloomKeep * bloomGain * lerp(1.0, 0.22, dustLane));
      alphaBloomFlow *= brightBloomSuppress;
      float alpha = lerp(alphaBaseFlow, alphaBloomFlow, bloomPass);
      float3 lit = lerp(baseLit, bloomLit, bloomPass);
      return float4(lit * alpha, alpha);
    }
    if (preset == 7.0) {
      float particleDim = max(0.12, uVisualA.x);
      float bgConfidence = saturate(uBackground.w);
      float brightBg = smoothstep(0.46, 0.86, saturate(uBackground.x)) * bgConfidence;
      float busyBg = smoothstep(0.24, 0.66, max(saturate(uBackground.y), saturate(uBackground.z))) * bgConfidence;
      float bgRead = saturate(max(brightBg, busyBg * 0.72));
      float jellyAirPx = max(uAudioB.w, uTreble * 0.08);
      float jellyPresencePx = max(uAudioB.y, uTreble * 0.10);
      float jellyBodyPx = max(uAudioA.y, uBass * 0.18);
      float3 baseLit = input.color.rgb * (0.84 + glow * 0.24 + jellyAirPx * 0.024 + jellyBodyPx * 0.012);
      float3 contrastTint = lerp(float3(0.025, 0.16, 0.27), float3(0.28, 0.055, 0.28), saturate(input.color.r + glow * 0.24));
      baseLit = lerp(baseLit, contrastTint + baseLit * 0.22, bgRead * 0.68);
      baseLit = lerp(baseLit, max(baseLit, float3(0.48, 0.88, 1.0) * 0.34), readableRim * (1.0 - brightBg) * 0.18);
      float alphaBaseJelly = saturate(input.color.a * uOpacity * particleDim * sphereAlpha * (0.86 + glow * 0.26 + jellyPresencePx * 0.10) * lerp(1.0, 1.82, bgRead));
      float alphaBloomJelly = saturate(input.color.a * uOpacity * max(0.0, uVisualB.w) * particleDim * outerGlowAlpha * (0.018 + glow * 0.040 + jellyAirPx * 0.026) * lerp(1.0, 0.34, bgRead));
      float alpha = lerp(alphaBaseJelly, alphaBloomJelly, bloomPass);
      float3 bloomLit = input.color.rgb * (0.52 + glow * 0.56 + jellyAirPx * 0.16) + uHighlight.rgb * (0.020 + glow * 0.040);
      float3 lit = lerp(baseLit, bloomLit, bloomPass);
      return float4(lit * alpha, alpha);
    }
    float alphaBase = saturate(input.color.a * uOpacity * max(0.12, uVisualA.x) * sphereAlpha * (0.96 + pulse * 0.12 + glow * 0.10));
    float alphaBloom = saturate(input.color.a * uOpacity * max(0.12, uVisualA.x) * outerGlowAlpha * (0.045 + bloom * 0.075 + glow * 0.065 + pulse * 0.035));
    float alpha = lerp(alphaBase, alphaBloom, bloomPass);
    float3 lit = input.color.rgb * (0.72 + pulse * 0.22 + glow * 0.24);
    float3 bloomLit = input.color.rgb * (0.88 + bloom * 0.86 + glow * 0.56) + uHighlight.rgb * (0.06 + glow * 0.12);
    lit = lerp(lit, bloomLit, bloomPass);
    if (preset == 6.0) lit = lerp(lit, uHighlight.rgb, saturate(input.aux * (0.18 + bloomPass * 0.22)));
    if (preset == 7.0) lit = lerp(lit, uSecondary.rgb + uHighlight.rgb * 0.18, saturate(input.aux * 0.08 + bloomPass * 0.14));
    return float4(lit * alpha, alpha);
  }
  float3 sampled = sampleCoverTile(input.uv, input.local, 1.0 / 167.0, 0.58, bloomPass).rgb;
  float4 edgeMap = uEdge.Sample(uCoverSampler, input.uv);
  float depthVal = edgeMap.r;
  float edgeVal = edgeMap.g;
  float fgMask = edgeMap.b;
  float lumVal = edgeMap.a;
  float3 theme = lerp(uPrimary.rgb, uSecondary.rgb, input.uv.y);
  float3 color = lerp(theme, sampled, saturate(uUseTexture));
  if (preset == 1.0) color = lerp(color, uHighlight.rgb, 0.28 + saturate(input.aux) * 0.18);
  float depthAlpha = input.color.a > 0.001 ? saturate(input.color.a) : (preset == 2.0 ? 0.0 : 1.0);
  float depthBright = input.color.a > 0.001 ? saturate(input.color.r) : 1.0;
  float presetAlphaMul = preset == 0.0 ? 0.95 : (preset == 2.0 ? 0.86 : (preset == 1.0 ? 1.12 : 1.0));
  float presetBloomMul = preset == 0.0 ? 0.34 : (preset == 2.0 ? 0.42 : (preset == 1.0 ? 0.52 : 1.0));
  float presetCore = preset == 0.0 ? (0.86 + sphereCore * 0.20 - sphereEdge * 0.12) : (preset == 2.0 ? (0.78 + sphereCore * 0.34 - sphereEdge * 0.16) : (preset == 1.0 ? (0.86 + sphereCore * 0.22 - sphereEdge * 0.10) : 1.0));
  float presetCoreBias = preset == 0.0 ? 0.014 : (preset == 2.0 ? 0.034 : (preset == 1.0 ? 0.020 : 0.0));
  float uvEdge = max(abs(input.uv.x - 0.5), abs(input.uv.y - 0.5)) * 2.0;
  float geometricEdge = smoothstep(0.62, 1.0, uvEdge);
  float textureEdge = saturate(edgeVal * (0.65 + edgePeak * 0.55 + presence * 0.32));
  float edgeGlow = max(geometricEdge * 0.28, textureEdge) * uVisualC.w;
  float cursorGlow = saturate(input.aux);
  float darkGuard = saturate((1.0 - lumVal) * (1.0 - fgMask) * saturate(uUseTexture));
  color = lerp(color, uHighlight.rgb, saturate(pulse * 0.48 + input.rnd * 0.12 + edgeGlow * 0.62 + cursorGlow * 0.36));
  float bloom = uVisualC.z * max(0.0, uVisualB.w);
  float coverPresence = lerp(1.0, 0.56 + fgMask * 0.54 + depthVal * 0.18, saturate(uUseTexture));
  float alphaBase = saturate((0.054 + presetCoreBias + pulse * 0.082 + input.rnd * 0.018 + bloom * 0.018 + edgeGlow * 0.040 + cursorGlow * 0.072) * uOpacity * max(0.12, uVisualA.x) * sphereAlpha * coverPresence * presetAlphaMul * depthAlpha * presetCore);
  float alphaBloom = saturate((0.016 + pulse * 0.026 + bloom * 0.032 + edgeGlow * 0.024 + cursorGlow * 0.036) * uOpacity * max(0.12, uVisualA.x) * outerGlowAlpha * coverPresence * (1.0 - darkGuard * 0.42) * presetAlphaMul * presetBloomMul * lerp(0.72, 1.0, depthAlpha));
  if (preset == 1.0 || preset == 2.0) {
    float playerAlphaScale = preset == 2.0 ? 0.64 : 0.48;
    float playerBloomScale = preset == 2.0 ? 0.26 : 0.30;
    float playerVAlpha = (0.92 + saturate(input.aux) * 0.10) * lerp(1.0, depthAlpha, preset == 2.0 ? 0.92 : 0.35);
    float playerSolidCore = preset == 2.0 ? saturate(sphereAlpha * 1.18 + sphereCore * 0.20 - sphereEdge * 0.10) : sphereAlpha;
    float playerMainAlpha = playerSolidCore * uOpacity * max(0.12, uVisualA.x) * coverPresence * playerVAlpha * playerAlphaScale;
    float playerBloomAlpha = outerGlowAlpha * uOpacity * max(0.12, uVisualA.x) * max(0.0, uVisualB.w) * coverPresence * (1.0 + saturate(input.aux) * 0.65) * 0.55 * playerBloomScale;
    alphaBase = saturate(lerp(alphaBase, playerMainAlpha, 0.82));
    alphaBloom = saturate(lerp(alphaBloom, playerBloomAlpha, 0.88));
  }
  float alpha = lerp(alphaBase, alphaBloom, bloomPass);
  if (preset == 3.0) alpha = 0.0;
  float shade = 0.20 + sphereLight * 1.02 + sphereFill * 0.16 + sphereCore * 0.52 - sphereEdge * 0.42;
  if (preset == 0.0) shade = 0.18 + sphereLight * 1.04 + sphereFill * 0.14 + sphereCore * 0.62 - sphereEdge * 0.50;
  if (preset == 1.0) shade = 0.18 + sphereLight * 1.08 + sphereFill * 0.14 + sphereCore * 0.58 - sphereEdge * 0.48;
  if (preset == 2.0) shade = 0.10 + sphereLight * 1.16 + sphereFill * 0.10 + sphereCore * 0.78 - sphereEdge * 0.66;
  float3 lit = (color * shade + uHighlight.rgb * sphereSpec * (0.34 + edgeGlow * 0.06 + cursorGlow * 0.08)) * (0.78 + pulse * 0.14 + bloom * 0.035 + depthVal * 0.05) * max(0.2, uVisualB.z) * lerp(0.58, 1.16, depthBright);
  float3 bloomLit = (color * (0.20 + pulse * 0.06 + bloom * 0.09 + edgeGlow * 0.07) + uHighlight.rgb * (0.010 + edgeGlow * 0.025 + sphereRim * 0.018)) * max(0.2, uVisualB.z);
  float outLum = dot(lit, float3(0.299, 0.587, 0.114));
  float lightParticle = smoothstep(0.50, 0.82, outLum) * (1.0 - darkGuard);
  float darkParticle = (1.0 - smoothstep(0.20, 0.50, outLum)) * (1.0 - darkGuard);
  lit = lerp(lit, float3(0.0, 0.0, 0.0), readableRim * lightParticle * 0.38);
  lit = lerp(lit, float3(1.0, 1.0, 1.0), readableRim * darkParticle * 0.20);
  lit = lerp(lit, bloomLit, bloomPass);
  lit *= 1.0 - darkGuard * saturate(uAudioC.w) * 0.32;
  return float4(lit * alpha, alpha);
}";

    private const string LyricVertexShaderSource = @"
cbuffer LyricParams : register(b0) {
  float uCenterX;
  float uCenterY;
  float uWidth;
  float uHeight;
  float uProgress;
  float uOpacity;
  float uBeat;
  float uBass;
  float uGlowStrength;
  float uHighBloom;
  float uBeatGlow;
  float uGlowParticles;
  float uTime;
  float uDepth;
  float uTiltX;
  float uTiltY;
  float uCameraLock;
  float uFeather;
  float uTextMin;
  float uTextMax;
  float4 uPrimary;
  float4 uHighlight;
  float4 uGlow;
  float4 uBackground;
};
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut main(uint id : SV_VertexID) {
  float2 corners[6] = {
    float2(-1.0, -1.0), float2(-1.0, 1.0), float2(1.0, -1.0),
    float2(1.0, -1.0), float2(-1.0, 1.0), float2(1.0, 1.0)
  };
  float2 uvs[6] = {
    float2(0.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0),
    float2(1.0, 1.0), float2(0.0, 0.0), float2(1.0, 0.0)
  };
  float2 local = corners[id] * float2(uWidth, uHeight) * 0.5;
  local.y += local.x * sin(uTiltY) * 0.035;
  float3 p3 = float3(local, uDepth * 0.26);
  float sx = sin(uTiltX), cx = cos(uTiltX);
  float sy = sin(uTiltY), cy = cos(uTiltY);
  p3.yz = float2(p3.y * cx - p3.z * sx, p3.y * sx + p3.z * cx);
  p3.xz = float2(p3.x * cy + p3.z * sy, -p3.x * sy + p3.z * cy);
  float depthScale = 1.0 / max(0.50, 1.0 + p3.z * 0.86 + abs(uDepth) * 0.045);
  depthScale = lerp(depthScale, saturate(depthScale * 0.94 + 0.06), saturate(uCameraLock));
  float2 p = float2(uCenterX, uCenterY) + p3.xy * depthScale;
  VSOut o;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = uvs[id];
  return o;
}";

    private const string LyricPixelShaderSource = @"
cbuffer LyricParams : register(b0) {
  float uCenterX;
  float uCenterY;
  float uWidth;
  float uHeight;
  float uProgress;
  float uOpacity;
  float uBeat;
  float uBass;
  float uGlowStrength;
  float uHighBloom;
  float uBeatGlow;
  float uGlowParticles;
  float uTime;
  float uDepth;
  float uTiltX;
  float uTiltY;
  float uCameraLock;
  float uFeather;
  float uTextMin;
  float uTextMax;
  float4 uPrimary;
  float4 uHighlight;
  float4 uGlow;
  float4 uBackground;
};
Texture2D uLyric : register(t0);
SamplerState uLyricSampler : register(s0);
struct PSIn { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(PSIn input) : SV_TARGET {
  float2 texel = float2(1.0 / max(uWidth * 1200.0, 256.0), 1.0 / max(uHeight * 1200.0, 128.0));
  float a = uLyric.Sample(uLyricSampler, input.uv).a;
  float glowA = a;
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2( 1.5, 0.0)).a * 0.72);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2(-1.5, 0.0)).a * 0.72);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2( 0.0, 1.5)).a * 0.72);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2( 0.0,-1.5)).a * 0.72);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2( 3.4, 2.2)).a * 0.42);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2(-3.4, 2.2)).a * 0.42);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2( 3.4,-2.2)).a * 0.42);
  glowA = max(glowA, uLyric.Sample(uLyricSampler, input.uv + texel * float2(-3.4,-2.2)).a * 0.42);
  float denom = max(0.001, uTextMax - uTextMin);
  float textP = clamp((input.uv.x - uTextMin) / denom, 0.0, 1.0);
  float feather = max(0.012, uFeather) + saturate(uBeatGlow + uHighBloom) * 0.018;
  float filled = 1.0 - smoothstep(uProgress, uProgress + feather, textP);
  float edge = 1.0 - smoothstep(0.0, feather * 2.8, abs(textP - uProgress));
  float pulse = saturate(uBeat * 0.85 + uBass * 0.35);
  float glowDrive = saturate(uGlowStrength * 1.35);
  float solar = saturate(uHighBloom * 0.62 + uBeatGlow * 0.44 + pulse * 0.12) * glowDrive;
  float bgConfidence = saturate(uBackground.w);
  float brightBg = smoothstep(0.46, 0.86, saturate(uBackground.x)) * bgConfidence;
  float busyBg = smoothstep(0.24, 0.66, max(saturate(uBackground.y), saturate(uBackground.z))) * bgConfidence;
  float bgRead = saturate(max(brightBg, busyBg * 0.74));
  float inText = smoothstep(0.0, 0.035, input.uv.x - uTextMin) * smoothstep(0.0, 0.035, uTextMax - input.uv.x);
  float river = pow(saturate(0.5 + 0.5 * sin(textP * 38.0 + uBeatGlow * 3.4 + input.uv.y * 8.0 + uTime * 0.42)), 7.0) * uGlowParticles * inText;
  float fillMix = smoothstep(0.0, 1.0, filled);
  float3 unsungColor = lerp(uPrimary.rgb * 0.50, uGlow.rgb * 0.34, 0.24);
  float3 sungColor = uHighlight.rgb * (1.05 + pulse * 0.12);
  float3 baseColor = lerp(unsungColor, sungColor, fillMix);
  float3 lyricContrast = lerp(float3(0.10, 0.18, 0.28), float3(0.26, 0.42, 0.56), fillMix);
  baseColor = lerp(baseColor, lyricContrast, bgRead * 0.46);
  float3 haloColor = lerp(uGlow.rgb * 0.78, uHighlight.rgb, saturate(edge * 0.46 + uBeatGlow * 0.18));
  baseColor = lerp(baseColor, baseColor + uHighlight.rgb * 0.20, solar * (0.16 + fillMix * 0.32));
  baseColor += haloColor * edge * (0.18 + solar * 0.12 + pulse * 0.14);
  float glyphAlpha = saturate(a * uOpacity * (0.60 + fillMix * 0.38 + edge * 0.20 + solar * 0.06));
  float haloAlpha = saturate((glowA - a * 0.46) * uOpacity * (0.10 + glowDrive * 0.26 + solar * 0.22));
  float readabilityBand = smoothstep(0.0, 0.070, input.uv.x - uTextMin) * smoothstep(0.0, 0.070, uTextMax - input.uv.x);
  float readabilityAlpha = saturate((glowA - a * 0.04) * uOpacity * readabilityBand * (0.070 + glowDrive * 0.120 + solar * 0.040 + bgRead * 0.170));
  float2 sunPos = float2(lerp(uTextMin, uTextMax, uProgress), 0.52 + sin(uTime * 0.37) * 0.035);
  float2 sunDelta = (input.uv - sunPos) * float2(1.0, 1.55);
  float sun = exp(-dot(sunDelta, sunDelta) / max(0.0008, 0.014 + solar * 0.055)) * solar;
  float sparkAlpha = river * glowA * uOpacity * (0.006 + solar * 0.040 + uBeatGlow * 0.016);
  float3 readabilityColor = lerp(float3(0.001, 0.003, 0.006), float3(0.0, 0.004, 0.012), bgRead) * readabilityAlpha;
  float3 color = readabilityColor + baseColor * glyphAlpha + haloColor * haloAlpha + uHighlight.rgb * sun * (0.020 + solar * 0.070) + haloColor * sparkAlpha;
  float alpha = saturate(readabilityAlpha + glyphAlpha + haloAlpha + sun * (0.014 + solar * 0.066) + sparkAlpha);
  return float4(color, alpha);
}";

    private const string LyricSparkVertexShaderSource = @"
cbuffer LyricParams : register(b0) {
  float uCenterX;
  float uCenterY;
  float uWidth;
  float uHeight;
  float uProgress;
  float uOpacity;
  float uBeat;
  float uBass;
  float uGlowStrength;
  float uHighBloom;
  float uBeatGlow;
  float uGlowParticles;
  float uTime;
  float uDepth;
  float uTiltX;
  float uTiltY;
  float uCameraLock;
  float uFeather;
  float uTextMin;
  float uTextMax;
  float4 uPrimary;
  float4 uHighlight;
  float4 uGlow;
};
struct VSIn {
  float2 pos : POSITION;
  float rnd : RANDOM;
  float layer : LAYER;
};
struct VSOut {
  float4 pos : SV_POSITION;
  float rnd : TEXCOORD0;
  float size : TEXCOORD1;
  float alpha : TEXCOORD2;
  float3 color : TEXCOORD3;
  float spin : TEXCOORD4;
};
float hash(float n) { return frac(sin(n) * 43758.5453); }
VSOut main(VSIn input) {
  VSOut o;
  float layer = input.layer;
  float speed = 0.055 + layer * 0.006 + uBass * 0.018;
  float phase = frac(input.pos.x + uTime * speed + input.rnd * 0.21);
  float progressPull = 1.0 - saturate(abs(phase - uProgress) / 0.24);
  float band = input.pos.y;
  float bloom = saturate(uHighBloom * 0.62 + uBeatGlow * 0.48 + uBeat * 0.20);
  float drift = sin(uTime * (0.42 + layer * 0.045) + input.rnd * 12.0) * (0.025 + bloom * 0.032);
  float lift = cos(uTime * (0.31 + input.rnd * 0.20) + phase * 7.0) * (0.018 + uBass * 0.020);
  float spread = 1.0 + bloom * 0.55 + uGlowStrength * 0.28;
  float textX = lerp(uTextMin, uTextMax, phase);
  float2 local = float2((textX - 0.5) * uWidth * (1.06 + layer * 0.015), (band * spread + drift + lift) * uHeight);
  local.y += local.x * sin(uTiltY) * 0.035;
  float3 p3 = float3(local, uDepth * 0.26);
  float sx = sin(uTiltX), cx = cos(uTiltX);
  float sy = sin(uTiltY), cy = cos(uTiltY);
  p3.yz = float2(p3.y * cx - p3.z * sx, p3.y * sx + p3.z * cx);
  p3.xz = float2(p3.x * cy + p3.z * sy, -p3.x * sy + p3.z * cy);
  float depthScale = 1.0 / max(0.50, 1.0 + p3.z * 0.86 + abs(uDepth) * 0.045);
  depthScale = lerp(depthScale, saturate(depthScale * 0.94 + 0.06), saturate(uCameraLock));
  float2 p = float2(uCenterX, uCenterY) + p3.xy * depthScale;
  float edgeFade = smoothstep(0.0, 0.12, phase) * smoothstep(1.0, 0.88, phase);
  float focus = 0.18 + progressPull * (0.72 + bloom * 0.18);
  float layerFade = lerp(0.72, 0.36, saturate(layer / 4.0));
  float alpha = edgeFade * focus * layerFade * uGlowParticles * uOpacity * saturate(0.15 + uGlowStrength * 1.18 + bloom * 0.42);
  float warm = saturate(progressPull * 0.52 + uBeatGlow * 0.20 + hash(input.rnd * 19.0) * 0.22);
  o.pos = float4(p, 0.0, 1.0);
  o.rnd = input.rnd;
  o.size = (0.0052 + hash(input.rnd * 29.0) * 0.0076 + bloom * 0.0065 + uBass * 0.0035) * (1.0 + layer * 0.070);
  o.alpha = alpha;
  o.color = lerp(uGlow.rgb, uHighlight.rgb, warm);
  o.spin = uTime * (0.45 + input.rnd * 0.55) + layer;
  return o;
}";

    private const string LyricSparkGeometryShaderSource = @"
cbuffer LyricParams : register(b0) {
  float uCenterX;
  float uCenterY;
  float uWidth;
  float uHeight;
  float uProgress;
  float uOpacity;
  float uBeat;
  float uBass;
  float uGlowStrength;
  float uHighBloom;
  float uBeatGlow;
  float uGlowParticles;
  float uTime;
  float uDepth;
  float uTiltX;
  float uTiltY;
  float uCameraLock;
  float uFeather;
  float uTextMin;
  float uTextMax;
  float4 uPrimary;
  float4 uHighlight;
  float4 uGlow;
};
struct VSOut {
  float4 pos : SV_POSITION;
  float rnd : TEXCOORD0;
  float size : TEXCOORD1;
  float alpha : TEXCOORD2;
  float3 color : TEXCOORD3;
  float spin : TEXCOORD4;
};
struct GSOut {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float alpha : TEXCOORD1;
  float3 color : TEXCOORD2;
  float rnd : TEXCOORD3;
};
[maxvertexcount(4)]
void main(point VSOut input[1], inout TriangleStream<GSOut> stream) {
  float s = input[0].size;
  float2 right = float2(cos(input[0].spin), sin(input[0].spin)) * s;
  float2 up = float2(-right.y, right.x) * (0.78 + input[0].rnd * 0.40);
  float2 corners[4] = { -right - up, -right + up, right - up, right + up };
  float2 uvs[4] = { float2(0.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0), float2(1.0, 0.0) };
  for (int i = 0; i < 4; i++) {
    GSOut o;
    o.pos = input[0].pos + float4(corners[i], 0.0, 0.0);
    o.uv = uvs[i];
    o.alpha = input[0].alpha;
    o.color = input[0].color;
    o.rnd = input[0].rnd;
    stream.Append(o);
  }
}";

    private const string LyricSparkPixelShaderSource = @"
struct PSIn {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD0;
  float alpha : TEXCOORD1;
  float3 color : TEXCOORD2;
  float rnd : TEXCOORD3;
};
float4 main(PSIn input) : SV_TARGET {
  float2 p = input.uv * 2.0 - 1.0;
  float d = dot(p, p);
  float core = exp(-d * 3.4);
  float ray = pow(saturate(1.0 - abs(p.x * p.y) * 2.2), 4.0) * saturate(1.0 - d);
  float alpha = saturate((core * 0.72 + ray * 0.20) * input.alpha);
  float3 color = input.color * alpha * (0.86 + core * 0.58);
  return float4(color, alpha);
}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleVertex
{
    public Vector2 Position;
    public Vector2 Uv;
    public float Random;
    public Vector3 Model;
    public float Part;
    public Vector4 Param;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LyricSparkVertex
{
    public Vector2 Position;
    public float Random;
    public float Layer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleConstants
{
    public float Time;
    public float Bass;
    public float Mid;
    public float Treble;
    public float Beat;
    public float Energy;
    public float Opacity;
    public float Preset;
    public float Width;
    public float Height;
    public float UseTexture;
    public float Padding;
    public Vector4 Primary;
    public Vector4 Secondary;
    public Vector4 Highlight;
    public Vector4 VisualA;
    public Vector4 VisualB;
    public Vector4 VisualC;
    public Vector4 VisualD;
    public Vector4 VisualE;
    public Vector4 AudioA;
    public Vector4 AudioB;
    public Vector4 AudioC;
    public Vector4 AudioD;
    public Vector4 Runtime;
    public Vector4 Background;
    public Vector4 Interaction;
    public Vector4 Ripple0;
    public Vector4 Ripple1;
    public Vector4 Ripple2;
    public Vector4 Ripple3;
    public Vector4 Ripple4;
    public Vector4 Ripple5;
    public Vector4 Ripple6;
    public Vector4 Ripple7;
    public Vector4 Ripple8;
    public Vector4 Ripple9;
    public Vector4 Ripple10;
    public Vector4 Ripple11;
    public Vector4 Hand;
    public Vector4 ViewControl;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LyricConstants
{
    public float CenterX;
    public float CenterY;
    public float Width;
    public float Height;
    public float Progress;
    public float Opacity;
    public float Beat;
    public float Bass;
    public float GlowStrength;
    public float HighBloom;
    public float BeatGlow;
    public float GlowParticles;
    public float Time;
    public float Depth;
    public float TiltX;
    public float TiltY;
    public float CameraLock;
    public float Padding0;
    public float Padding1;
    public float Padding2;
    public Vector4 Primary;
    public Vector4 Highlight;
    public Vector4 Glow;
    public Vector4 Background;
}

internal sealed class WallpaperState
{
    public bool Enabled = true;
    public bool Playing = true;
    public int Preset = 5;
    public float Opacity = 1f;
    public float Bass = .10f;
    public float Mid = .08f;
    public float Treble = .08f;
    public float Beat = .04f;
    public float Energy = .10f;
    public float SubBass = 0f;
    public float BassBody = 0f;
    public float LowMid = 0f;
    public float MidFlow = 0f;
    public float HighMid = 0f;
    public float Presence = 0f;
    public float Brilliance = 0f;
    public float Air = 0f;
    public bool HasSonicBands = false;
    public float SonicSubBass = 0f;
    public float SonicBassBody = 0f;
    public float SonicLowMid = 0f;
    public float SonicMidFlow = 0f;
    public float SonicHighMid = 0f;
    public float SonicPresence = 0f;
    public float SonicBrilliance = 0f;
    public float SonicAir = 0f;
    public float KickEnvelope = 0f;
    public float KickConfidence = 0f;
    public int BeatSerial = 0;
    public float BeatStrength = 0f;
    public float BeatImpact = 0f;
    public string BeatCombo = "beat";
    public float BeatPhase = 0f;
    public float BeatTempoConfidence = 0f;
    public float Warmth = 0f;
    public float Brightness = 0f;
    public float VisualStructure = 0f;
    public float VisualMotion = 0f;
    public float VisualImpact = 0f;
    public float VisualImpactPulse = 0f;
    public float VisualImpactAge = 1f;
    public float VisualImpactStrength = 0f;
    public float VisualGloss = 0f;
    public string Texture = "";
    public string Quality = "balanced";
    public int TargetFps = 60;
    public bool LyricEnabled = true;
    public string LyricText = "";
    public float LyricProgress = 0f;
    public float LyricFeather = .055f;
    public float LyricScale = 1f;
    public float LyricOffsetX = 0f;
    public float LyricOffsetY = 0f;
    public float LyricOffsetZ = 0f;
    public float LyricTiltX = 0f;
    public float LyricTiltY = 0f;
    public bool LyricCameraLock = false;
    public bool LyricCinema = true;
    public int LyricWeight = 900;
    public string LyricFont = "hei";
    public bool LyricGlow = true;
    public bool LyricGlowBeat = true;
    public bool LyricGlowParticles = false;
    public float LyricGlowStrength = .28f;
    public float LyricHighBloom = 0f;
    public float LyricBeatGlow = 0f;
    public float VisualIntensity = 1f;
    public float VisualSpeed = .76f;
    public float VisualPoint = .82f;
    public float VisualScatter = .2f;
    public float VisualCoverResolution = 1f;
    public float VisualCoverGrid = .85f;
    public float VisualTwist = .22f;
    public float VisualDepth = 1.22f;
    public float VisualColor = 1.12f;
    public float VisualBloom = .74f;
    public float VisualBgFade = .6f;
    public float VisualCinemaShake = .5f;
    public bool VisualCinema = true;
    public bool VisualBloomEnabled = true;
    public bool VisualEdge = false;
    public float VisualEqLowBreath = 1f;
    public float VisualEqLowWeight = 1f;
    public float VisualEqSlowFlow = 1f;
    public float VisualEqMidDirection = 1f;
    public float VisualEqEdgePeak = 1f;
    public float VisualEqSparkle = 1f;
    public float VisualEqAirDust = 1f;
    public float VisualEqKickSoft = 1f;
    public float VisualEqKickRelease = 1f;
    public float HandActive = 0f;
    public float HandX = -999f;
    public float HandY = -999f;
    public float GestureGrip = 0f;
    public float BackgroundLuma = 0f;
    public float BackgroundContrast = 0f;
    public float BackgroundBusy = 0f;
    public float BackgroundConfidence = 0f;
    public Vector4 Primary = Rgba("#d6f8ff");
    public Vector4 Secondary = Rgba("#9cffdf");
    public Vector4 Highlight = Rgba("#fff0b8");
    public Vector4 Glow = Rgba("#9cffdf");
    public Vector4 LyricPrimary = Rgba("#d6f8ff");
    public Vector4 LyricSecondary = Rgba("#9cffdf");
    public Vector4 LyricHighlight = Rgba("#fff0b8");
    public Vector4 LyricGlowColor = Rgba("#9cffdf");

    public WallpaperState Clone()
    {
        return (MemberwiseClone() as WallpaperState) ?? new WallpaperState();
    }

    private static float CompressMacro(float value, float knee)
    {
        var x = MathF.Max(0f, value);
        var k = MathF.Max(0.001f, knee);
        return x / (x + k);
    }

    public static WallpaperState FromJson(JsonElement root, WallpaperState previous)
    {
        var state = previous.MemberwiseClone() as WallpaperState ?? new WallpaperState();
        state.Enabled = root.GetBool("enabled", state.Enabled);
        state.Playing = root.GetBool("playing", state.Playing);
        state.Preset = SanitizePreset(root.GetPreset("preset", state.Preset));
        state.Opacity = Math.Clamp(root.GetFloat("opacity", state.Opacity), .05f, 1f);
        state.Texture = root.GetString("texture") ?? state.Texture;
        state.Quality = NormalizeQuality(root.GetString("quality") ?? state.Quality);
        state.TargetFps = NormalizeTargetFps(root.GetString("frameRate"), root.GetInt("throttleFps", state.TargetFps));
        if (root.TryGetProperty("lyric", out var lyric))
        {
            state.LyricEnabled = lyric.GetBool("enabled", state.LyricEnabled);
            state.LyricText = lyric.GetString("text") ?? state.LyricText;
            state.LyricProgress = Math.Clamp(lyric.GetFloat("progress", state.LyricProgress), 0f, 1f);
            state.LyricFeather = Math.Clamp(lyric.GetFloat("feather", state.LyricFeather), .012f, .090f);
        }
        JsonElement motion = default;
        var hasMotionSource = false;
        if (root.TryGetProperty("motion", out var motionProp) && motionProp.ValueKind == JsonValueKind.Object)
        {
            motion = motionProp;
            hasMotionSource = true;
        }
        else if (root.TryGetProperty("audio", out var audioProp) && audioProp.ValueKind == JsonValueKind.Object)
        {
            motion = audioProp;
            hasMotionSource = true;
        }
        else if (root.TryGetProperty("bass", out _) || root.TryGetProperty("subBass", out _) || root.TryGetProperty("kickEnvelope", out _))
        {
            motion = root;
            hasMotionSource = true;
        }
        if (hasMotionSource)
        {
            state.Bass = motion.GetFloat("bass", state.Bass);
            state.Mid = motion.GetFloat("mid", state.Mid);
            state.Treble = motion.GetFloat("treble", state.Treble);
            state.Beat = motion.GetFloat("beat", state.Beat);
            state.Energy = motion.GetFloat("energy", state.Energy);
            state.SubBass = Math.Clamp(motion.GetFloat("subBass", state.Bass * 0.72f), 0f, 1.2f);
            state.BassBody = Math.Clamp(motion.GetFloat("bassBody", state.Bass), 0f, 1.2f);
            state.LowMid = Math.Clamp(motion.GetFloat("lowMid", MathF.Max(state.Mid * 0.70f, state.Bass * 0.18f)), 0f, 1.2f);
            state.MidFlow = Math.Clamp(motion.GetFloat("midFlow", state.Mid), 0f, 1.2f);
            state.HighMid = Math.Clamp(motion.GetFloat("highMid", state.Treble * 0.72f), 0f, 1.2f);
            state.Presence = Math.Clamp(motion.GetFloat("presence", state.Treble), 0f, 1.2f);
            state.Brilliance = Math.Clamp(motion.GetFloat("brilliance", state.Treble * 0.88f), 0f, 1.2f);
            state.Air = Math.Clamp(motion.GetFloat("air", state.Treble * 0.54f), 0f, 1.2f);
            state.HasSonicBands = motion.TryGetProperty("sonicSubBass", out _);
            if (state.HasSonicBands)
            {
                state.SonicSubBass = Math.Clamp(motion.GetFloat("sonicSubBass", 0f), 0f, 1.2f);
                state.SonicBassBody = Math.Clamp(motion.GetFloat("sonicBassBody", 0f), 0f, 1.2f);
                state.SonicLowMid = Math.Clamp(motion.GetFloat("sonicLowMid", 0f), 0f, 1.2f);
                state.SonicMidFlow = Math.Clamp(motion.GetFloat("sonicMidFlow", 0f), 0f, 1.2f);
                state.SonicHighMid = Math.Clamp(motion.GetFloat("sonicHighMid", 0f), 0f, 1.2f);
                state.SonicPresence = Math.Clamp(motion.GetFloat("sonicPresence", 0f), 0f, 1.2f);
                state.SonicBrilliance = Math.Clamp(motion.GetFloat("sonicBrilliance", 0f), 0f, 1.2f);
                state.SonicAir = Math.Clamp(motion.GetFloat("sonicAir", 0f), 0f, 1.2f);
            }
            state.KickEnvelope = Math.Clamp(motion.GetFloat("kickEnvelope", state.Beat), 0f, 1.4f);
            state.KickConfidence = Math.Clamp(motion.GetFloat("kickConfidence", MathF.Min(1.2f, state.Beat * 0.86f + state.Energy * 0.18f)), 0f, 1.2f);
            state.BeatSerial = Math.Max(0, motion.GetInt("beatSerial", state.BeatSerial));
            state.BeatStrength = Math.Clamp(motion.GetFloat("beatStrength", state.BeatStrength), 0f, 1f);
            state.BeatImpact = Math.Clamp(motion.GetFloat("beatImpact", state.BeatImpact), 0f, 1f);
            state.BeatCombo = motion.GetString("beatCombo") ?? state.BeatCombo;
            state.BeatPhase = Math.Clamp(motion.GetFloat("beatPhase", state.BeatPhase), 0f, 1f);
            state.BeatTempoConfidence = Math.Clamp(motion.GetFloat("tempoConfidence", state.BeatTempoConfidence), 0f, 1f);
            state.Warmth = Math.Clamp(motion.GetFloat("warmth", state.Warmth), 0f, 1f);
            state.Brightness = Math.Clamp(motion.GetFloat("brightness", state.Brightness), 0f, 1f);
            var fallbackStructure = CompressMacro(state.SubBass * 0.55f + state.BassBody * 0.70f, 0.42f);
            var fallbackMotion = CompressMacro(state.LowMid * 0.35f + state.MidFlow * 0.58f + state.HighMid * 0.12f, 0.48f);
            var fallbackGloss = CompressMacro(state.Presence * 0.34f + state.Brilliance * 0.36f + state.Air * 0.30f, 0.38f);
            var fallbackImpact = CompressMacro(state.KickEnvelope * MathF.Max(0.28f, state.KickConfidence), 0.30f);
            var previousImpact = state.VisualImpact;
            var previousPulse = state.VisualImpactPulse;
            var hasImpactPulse = motion.TryGetProperty("impactPulse", out _);
            state.VisualStructure = Math.Clamp(motion.GetFloat("structure", fallbackStructure), 0f, 1.2f);
            state.VisualMotion = Math.Clamp(motion.GetFloat("motionFlow", fallbackMotion), 0f, 1.2f);
            state.VisualImpact = Math.Clamp(motion.GetFloat("impact", fallbackImpact), 0f, 1.2f);
            var incomingPulse = hasImpactPulse
                ? Math.Clamp(motion.GetFloat("impactPulse", previousPulse), 0f, 1.2f)
                : 0f;
            var fallbackPulse = fallbackImpact > 0.34f && (previousImpact <= 0.30f || fallbackImpact - previousImpact > 0.10f)
                ? Math.Clamp(fallbackImpact * 1.35f, 0f, 1.2f)
                : previousPulse * 0.58f;
            state.VisualImpactPulse = Math.Clamp(MathF.Max(incomingPulse, fallbackPulse), 0f, 1.2f);
            state.VisualImpactAge = Math.Clamp(motion.GetFloat("impactAge", state.VisualImpactAge), 0f, 4f);
            state.VisualImpactStrength = Math.Clamp(motion.GetFloat("impactStrength", fallbackImpact), 0f, 1.2f);
            state.VisualGloss = Math.Clamp(motion.GetFloat("gloss", fallbackGloss), 0f, 1.2f);
        }
        if (root.TryGetProperty("colors", out var colors))
        {
            state.Primary = Rgba(colors.GetString("primary"), state.Primary);
            state.Secondary = Rgba(colors.GetString("secondary"), state.Secondary);
            state.Highlight = Rgba(colors.GetString("highlight"), state.Highlight);
            state.Glow = Rgba(colors.GetString("glow"), state.Glow);
        }
        if (root.TryGetProperty("lyricColors", out var lyricColors))
        {
            state.LyricPrimary = Rgba(lyricColors.GetString("primary"), state.LyricPrimary);
            state.LyricSecondary = Rgba(lyricColors.GetString("secondary"), state.LyricSecondary);
            state.LyricHighlight = Rgba(lyricColors.GetString("highlight"), state.LyricHighlight);
            state.LyricGlowColor = Rgba(lyricColors.GetString("glow"), state.LyricGlowColor);
        }
        else if (root.TryGetProperty("colors", out var fallbackColors))
        {
            state.LyricPrimary = Rgba(fallbackColors.GetString("primary"), state.LyricPrimary);
            state.LyricSecondary = Rgba(fallbackColors.GetString("secondary"), state.LyricSecondary);
            state.LyricHighlight = Rgba(fallbackColors.GetString("highlight"), state.LyricHighlight);
            state.LyricGlowColor = Rgba(fallbackColors.GetString("glow"), state.LyricGlowColor);
        }
        if (root.TryGetProperty("background", out var background))
        {
            var backgroundLuma = background.GetFloat("luma", float.NaN);
            if (float.IsNaN(backgroundLuma)) backgroundLuma = background.GetFloat("luminance", state.BackgroundLuma);
            state.BackgroundLuma = Math.Clamp(backgroundLuma, 0f, 1f);
            state.BackgroundContrast = Math.Clamp(background.GetFloat("contrast", state.BackgroundContrast), 0f, 1f);
            state.BackgroundBusy = Math.Clamp(background.GetFloat("busy", state.BackgroundBusy), 0f, 1f);
            state.BackgroundConfidence = Math.Clamp(background.GetFloat("confidence", state.BackgroundConfidence), 0f, 1f);
        }
        if (root.TryGetProperty("visual", out var visual))
        {
            state.Preset = SanitizePreset(visual.GetPreset("preset", state.Preset));
            state.VisualIntensity = Math.Clamp(visual.GetFloat("intensity", state.VisualIntensity), .15f, 2.2f);
            state.VisualSpeed = Math.Clamp(visual.GetFloat("speed", state.VisualSpeed), .08f, 2.4f);
            state.VisualPoint = Math.Clamp(visual.GetFloat("point", state.VisualPoint), .12f, 2.8f);
            state.VisualScatter = Math.Clamp(visual.GetFloat("scatter", state.VisualScatter), 0f, 2f);
            state.VisualCoverResolution = Math.Clamp(visual.GetFloat("coverResolution", state.VisualCoverResolution), .35f, 1.8f);
            state.VisualCoverGrid = Math.Clamp(visual.GetFloat("coverGrid", state.VisualCoverGrid), 0f, 1f);
            state.VisualTwist = Math.Clamp(visual.GetFloat("twist", state.VisualTwist), -2f, 2f);
            state.VisualDepth = Math.Clamp(visual.GetFloat("depth", state.VisualDepth), .1f, 2.6f);
            state.VisualColor = Math.Clamp(visual.GetFloat("color", state.VisualColor), .1f, 2.4f);
            state.VisualBloom = Math.Clamp(visual.GetFloat("bloomStrength", state.VisualBloom), 0f, 2.5f);
            state.VisualBgFade = Math.Clamp(visual.GetFloat("bgFade", state.VisualBgFade), 0f, 1.2f);
            state.VisualCinemaShake = Math.Clamp(visual.GetFloat("cinemaShake", state.VisualCinemaShake), 0f, 1.8f);
            state.VisualCinema = visual.GetBool("cinema", state.VisualCinema);
            state.VisualBloomEnabled = visual.GetBool("bloom", state.VisualBloomEnabled);
            state.VisualEdge = visual.GetBool("edge", state.VisualEdge);
            state.VisualEqLowBreath = Math.Clamp(visual.GetFloat("visualEqLowBreath", state.VisualEqLowBreath), 0f, 2f);
            state.VisualEqLowWeight = Math.Clamp(visual.GetFloat("visualEqLowWeight", state.VisualEqLowWeight), 0f, 2f);
            state.VisualEqSlowFlow = Math.Clamp(visual.GetFloat("visualEqSlowFlow", state.VisualEqSlowFlow), 0f, 2f);
            state.VisualEqMidDirection = Math.Clamp(visual.GetFloat("visualEqMidDirection", state.VisualEqMidDirection), 0f, 2f);
            state.VisualEqEdgePeak = Math.Clamp(visual.GetFloat("visualEqEdgePeak", state.VisualEqEdgePeak), 0f, 2f);
            state.VisualEqSparkle = Math.Clamp(visual.GetFloat("visualEqSparkle", state.VisualEqSparkle), 0f, 2f);
            state.VisualEqAirDust = Math.Clamp(visual.GetFloat("visualEqAirDust", state.VisualEqAirDust), 0f, 2f);
            state.VisualEqKickSoft = Math.Clamp(visual.GetFloat("visualEqKickSoft", state.VisualEqKickSoft), 0f, 2f);
            state.VisualEqKickRelease = Math.Clamp(visual.GetFloat("visualEqKickRelease", state.VisualEqKickRelease), .35f, 1.85f);
            state.LyricScale = Math.Clamp(visual.GetFloat("lyricScale", state.LyricScale), .35f, 1.65f);
            state.LyricOffsetX = Math.Clamp(visual.GetFloat("lyricOffsetX", state.LyricOffsetX), -2f, 2f);
            state.LyricOffsetY = Math.Clamp(visual.GetFloat("lyricOffsetY", state.LyricOffsetY), -1.2f, 1.35f);
            state.LyricOffsetZ = Math.Clamp(visual.GetFloat("lyricOffsetZ", state.LyricOffsetZ), -1.6f, 1.6f);
            state.LyricTiltX = Math.Clamp(visual.GetFloat("lyricTiltX", state.LyricTiltX), -42f, 42f);
            state.LyricTiltY = Math.Clamp(visual.GetFloat("lyricTiltY", state.LyricTiltY), -42f, 42f);
            state.LyricCameraLock = visual.GetBool("lyricCameraLock", state.LyricCameraLock);
            state.LyricCinema = visual.GetBool("lyricCinema", state.LyricCinema);
            state.LyricWeight = Math.Clamp(visual.GetInt("lyricWeight", state.LyricWeight), 500, 900);
            state.LyricFont = visual.GetString("lyricFont") ?? state.LyricFont;
            state.LyricGlow = visual.GetBool("lyricGlow", state.LyricGlow);
            state.LyricGlowBeat = visual.GetBool("lyricGlowBeat", state.LyricGlowBeat);
            state.LyricGlowParticles = visual.GetBool("lyricGlowParticles", state.LyricGlowParticles);
            state.LyricGlowStrength = Math.Clamp(visual.GetFloat("lyricGlowStrength", state.LyricGlowStrength), 0f, .85f);
            state.LyricHighBloom = Math.Clamp(visual.GetFloat("lyricHighBloom", state.LyricHighBloom), 0f, 1.45f);
            state.LyricBeatGlow = Math.Clamp(visual.GetFloat("lyricBeatGlow", state.LyricBeatGlow), 0f, 1.7f);
            state.HandActive = Math.Clamp(visual.GetFloat("handActive", state.HandActive), 0f, 1f);
            state.HandX = Math.Clamp(visual.GetFloat("handX", state.HandX), -1.35f, 1.35f);
            state.HandY = Math.Clamp(visual.GetFloat("handY", state.HandY), -1.35f, 1.35f);
            state.GestureGrip = Math.Clamp(visual.GetFloat("gestureGrip", state.GestureGrip), 0f, 1f);
        }
        return state;
    }

    private static string NormalizeQuality(string? quality)
    {
        return (quality ?? "").Trim().ToLowerInvariant() switch
        {
            "eco" or "low" => "eco",
            "balanced" or "medium" or "med" => "balanced",
            "high" => "high",
            "ultra" => "ultra",
            _ => "balanced",
        };
    }

    private static int NormalizeTargetFps(string? frameRate, int throttleFps)
    {
        var normalized = (frameRate ?? "").Trim().ToLowerInvariant();
        if (normalized != "auto" && int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selected))
        {
            return selected is 30 or 60 or 90 or 120 ? selected : 60;
        }
        return Math.Clamp(throttleFps, 12, 120);
    }

    private static int SanitizePreset(int preset)
    {
        preset = Math.Clamp(preset, 0, 7);
        return preset == 6 ? 5 : preset;
    }

    private static Vector4 Rgba(string? value, Vector4 fallback = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback == default ? Vector4.One : fallback;
        try
        {
            var c = ColorTranslator.FromHtml(value);
            return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
        }
        catch
        {
            return fallback == default ? Vector4.One : fallback;
        }
    }
}

internal static class JsonExt
{
    public static string? GetString(this JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static bool GetBool(this JsonElement el, string name, bool fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    public static float GetFloat(this JsonElement el, string name, float fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return fallback;

        float n;
        if (v.ValueKind == JsonValueKind.Number)
        {
            try
            {
                if (!v.TryGetSingle(out n)) return fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
        else if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return fallback;
        }
        else
        {
            return fallback;
        }

        return float.IsNaN(n) || float.IsInfinity(n) ? fallback : n;
    }

    public static int GetInt(this JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return fallback;
        if (v.ValueKind == JsonValueKind.Number)
        {
            try
            {
                return v.TryGetInt32(out var n) ? n : fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
        if (v.ValueKind != JsonValueKind.String) return fallback;
        return int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    public static int GetPreset(this JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return fallback;
        if (v.ValueKind == JsonValueKind.Number)
        {
            try
            {
                if (v.TryGetInt32(out var n)) return n is 6 or 7 ? 5 : n;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
        if (v.ValueKind != JsonValueKind.String) return fallback;
        var presetName = (v.GetString() ?? "").Trim().ToLowerInvariant();
        if (int.TryParse(presetName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericPreset))
            return numericPreset is 6 or 7 ? 5 : numericPreset;
        if (presetName is "emily" or "cover" or "album" or "album-cover") return 0;
        return presetName switch
        {
            "tunnel" or "roll" or "roller" or "scroll" or "滚筒" => 1,
            "orbit" or "planet" or "sphere" or "starball" or "星球" => 2,
            "void" or "empty" or "虚空" => 3,
            "vinyl" or "record" or "disc" or "唱片" => 4,
            "galaxy" or "stars" or "starfield" or "星河" => 5,
            "skull" or "soul" or "requiem" or "安魂" => 5,
            "jelly" or "jellyfish" or "水母" => 5,
            _ => fallback,
        };
    }
}

internal sealed class StopwatchLite
{
    private long _start;
    public void Start() => _start = Environment.TickCount64;
    public float ElapsedSeconds => (Environment.TickCount64 - _start) / 1000f;
}

internal readonly struct DesktopAttachTarget
{
    public DesktopAttachTarget(IntPtr parent, IntPtr wallpaperHost, string key)
    {
        Parent = parent;
        WallpaperHost = wallpaperHost;
        Key = key;
    }
    public IntPtr Parent { get; }
    public IntPtr WallpaperHost { get; }
    public string Key { get; }
}

internal sealed class DesktopMouseWheelHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private readonly Action<int, Point> _onWheel;
    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct HookPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public HookPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    public DesktopMouseWheelHook(Action<int, Point> onWheel)
    {
        _onWheel = onWheel;
        _callback = HookCallback;
        try { _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0); }
        catch { _hook = IntPtr.Zero; }
    }

    public bool IsActive => _hook != IntPtr.Zero;

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == WmMouseWheel)
        {
            try
            {
                var info = Marshal.PtrToStructure<MouseHookData>(data);
                var delta = unchecked((short)((info.MouseData >> 16) & 0xffff));
                if (delta != 0) _onWheel(delta, new Point(info.Point.X, info.Point.Y));
            }
            catch {}
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hook); } catch {}
        _hook = IntPtr.Zero;
    }
}

internal static class Native
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;
    private static readonly IntPtr HWND_TOP = new(0);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    public static void BeginHighResolutionTimer()
    {
        try { timeBeginPeriod(1); } catch {}
    }

    public static void EndHighResolutionTimer()
    {
        try { timeEndPeriod(1); } catch {}
    }

    public static Rectangle DesktopBounds()
    {
        var progman = FindProgman();
        if (progman != IntPtr.Zero && GetClientRect(progman, out var rect))
        {
            return new Rectangle(0, 0, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        }
        return SystemInformation.VirtualScreen;
    }

    public static IntPtr ParentOf(IntPtr hwnd) => GetParent(hwnd);
    public static bool IsWindowHandle(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static void ShowTopMost(IntPtr hwnd, Rectangle bounds)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    public static void ShowOverlayFallback(IntPtr hwnd, Rectangle bounds)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_CHILD) | WS_POPUP | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);

        SetParent(hwnd, IntPtr.Zero);
        SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        ApplyDesktopOverlayRegion(hwnd, bounds);
    }

    public static void ShowDesktopOverlay(IntPtr hwnd, Rectangle bounds) => ShowOverlayFallback(hwnd, bounds);

    public static void RefreshDesktopOverlayRegion(IntPtr hwnd, Rectangle bounds)
    {
        if (hwnd == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0) return;
        ApplyDesktopOverlayRegion(hwnd, bounds);
    }

    private static void ApplyDesktopOverlayRegion(IntPtr hwnd, Rectangle bounds)
    {
        const int RGN_DIFF = 4;
        var region = CreateRectRgn(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        if (region == IntPtr.Zero) return;

        SubtractTaskbarAndTrayRegions(region, bounds, RGN_DIFF);

        if (!SetWindowRgn(hwnd, region, true))
        {
            DeleteObject(region);
        }
    }

    private static void SubtractTaskbarAndTrayRegions(IntPtr region, Rectangle bounds, int diffMode)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var screenBounds = screen.Bounds;
            var workingArea = screen.WorkingArea;
            if (workingArea.Left > screenBounds.Left)
            {
                SubtractAbsoluteRectFromRegion(region, bounds, Rectangle.FromLTRB(screenBounds.Left, screenBounds.Top, workingArea.Left, screenBounds.Bottom), diffMode, 1);
            }
            if (workingArea.Right < screenBounds.Right)
            {
                SubtractAbsoluteRectFromRegion(region, bounds, Rectangle.FromLTRB(workingArea.Right, screenBounds.Top, screenBounds.Right, screenBounds.Bottom), diffMode, 1);
            }
            if (workingArea.Top > screenBounds.Top)
            {
                SubtractAbsoluteRectFromRegion(region, bounds, Rectangle.FromLTRB(screenBounds.Left, screenBounds.Top, screenBounds.Right, workingArea.Top), diffMode, 1);
            }
            if (workingArea.Bottom < screenBounds.Bottom)
            {
                SubtractAbsoluteRectFromRegion(region, bounds, Rectangle.FromLTRB(screenBounds.Left, workingArea.Bottom, screenBounds.Right, screenBounds.Bottom), diffMode, 1);
            }
        }

        EnumWindows((other, _) =>
        {
            if (other == IntPtr.Zero) return true;
            var cls = ClassName(other);
            var title = WindowText(other);
            if (!IsTaskbarOrTraySurface(cls, title)) return true;
            if (!GetWindowRect(other, out var rect)) return true;
            SubtractAbsoluteRectFromRegion(region, bounds, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), diffMode, 1);
            return true;
        }, IntPtr.Zero);
    }

    private static void SubtractAbsoluteRectFromRegion(IntPtr region, Rectangle bounds, Rectangle absoluteRect, int diffMode, int minSize)
    {
        var left = Math.Max(0, absoluteRect.Left - bounds.X);
        var top = Math.Max(0, absoluteRect.Top - bounds.Y);
        var right = Math.Min(bounds.Width, absoluteRect.Right - bounds.X);
        var bottom = Math.Min(bounds.Height, absoluteRect.Bottom - bounds.Y);
        if (right <= left || bottom <= top) return;
        if ((right - left) < minSize || (bottom - top) < minSize) return;

        var cut = CreateRectRgn(left, top, right, bottom);
        if (cut == IntPtr.Zero) return;
        CombineRgn(region, region, cut, diffMode);
        DeleteObject(cut);
    }

    private static bool IsTaskbarOrTraySurface(string cls, string title)
    {
        cls ??= "";
        title ??= "";
        return string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("TrayWnd", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Taskbar", StringComparison.OrdinalIgnoreCase)
            || title.Contains("任务栏", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDesktopWallpaperOrTransparentSystemSurface(string cls, string title)
    {
        if (string.IsNullOrWhiteSpace(cls)) return false;
        return string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "SysListView32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "CEF-OSC-WIDGET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("WPE", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Wallpaper", StringComparison.OrdinalIgnoreCase)
            || title.Contains("NVIDIA GeForce Overlay", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Windows 输入体验", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Windows Input Experience", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetCursorPosition(out Point point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new Point(nativePoint.X, nativePoint.Y);
            return true;
        }
        point = Point.Empty;
        return false;
    }

    public static bool IsPrimaryButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    }

    public static bool IsDesktopInteractionPoint(Point point)
    {
        var nativePoint = new POINT { X = point.X, Y = point.Y };
        var hwnd = WindowFromPoint(nativePoint);
        if (hwnd == IntPtr.Zero) return true;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var cls = ClassName(hwnd);
        var rootCls = ClassName(root);
        var title = WindowText(root);
        return IsDesktopWallpaperOrTransparentSystemSurface(cls, title)
            || IsDesktopWallpaperOrTransparentSystemSurface(rootCls, title);
    }

    public static DesktopAttachTarget ResolveDesktopAttachTarget()
    {
        var progman = FindProgman();
        if (progman == IntPtr.Zero) return new DesktopAttachTarget(IntPtr.Zero, IntPtr.Zero, "none");
        var iconHost = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var wpeHost = FindWallpaperEngineHost();
        return new DesktopAttachTarget(progman, wpeHost, $"Progman:0x{progman.ToInt64():X}/wpe:0x{wpeHost.ToInt64():X}/icons:0x{iconHost.ToInt64():X}");
    }

    public static void AttachToDesktopLayer(IntPtr hwnd, DesktopAttachTarget target, Rectangle bounds)
    {
        if (target.Parent == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~WS_EX_LAYERED;
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        if (target.WallpaperHost != IntPtr.Zero)
        {
            SetWindowPos(target.WallpaperHost, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        SetParent(hwnd, target.Parent);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    private static IntPtr FindProgman()
    {
        var progman = FindWindow("Progman", "Program Manager");
        return progman != IntPtr.Zero ? progman : FindWindow("Progman", null);
    }

    private static IntPtr FindWallpaperEngineHost()
    {
        var progman = FindProgman();
        if (progman != IntPtr.Zero)
        {
            var progmanWorker = FindDescendantWorkerWithWallpaperEngine(progman);
            if (progmanWorker != IntPtr.Zero) return progmanWorker;
        }

        var result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (result != IntPtr.Zero) return false;
            if (IsWorkerWindow(hwnd) && HasWallpaperEngineDescendant(hwnd))
            {
                result = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindDescendantWorkerWithWallpaperEngine(IntPtr root)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(root, (hwnd, _) =>
        {
            if (result != IntPtr.Zero) return false;
            if (IsWorkerWindow(hwnd) && HasWallpaperEngineDescendant(hwnd))
            {
                result = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool HasWallpaperEngineDescendant(IntPtr root)
    {
        var found = false;
        EnumChildWindows(root, (hwnd, _) =>
        {
            if (IsWallpaperEngineWindow(hwnd))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsWorkerWindow(IntPtr hwnd)
    {
        return string.Equals(ClassName(hwnd), "WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWallpaperEngineWindow(IntPtr hwnd)
    {
        var cls = ClassName(hwnd);
        var title = WindowText(hwnd);
        if (cls.StartsWith("WPE", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.IndexOf("Wallpaper", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (title.IndexOf("Wallpaper", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        var processName = ProcessName(hwnd);
        return processName.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0
            || processName.IndexOf("webwallpaper", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0) return "";
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName ?? "";
        }
        catch
        {
            return "";
        }
    }
}
