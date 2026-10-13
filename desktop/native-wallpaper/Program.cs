using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
var clipIndex = Array.FindIndex(args, arg => string.Equals(arg, "--clip-window", StringComparison.OrdinalIgnoreCase));
if (clipIndex >= 0 && args.Length > clipIndex + 1 && long.TryParse(args[clipIndex + 1], out var clipHwnd))
{
    Application.Run(new WallpaperClipContext(new IntPtr(clipHwnd)));
    return;
}
var statePath = args.Length > 0 ? args[0] : Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Mineradio",
    "wallpaper-state.json");
var attachToDesktop = !args.Any(arg => string.Equals(arg, "--no-parent", StringComparison.OrdinalIgnoreCase));
Application.Run(new WallpaperForm(statePath, attachToDesktop));

internal sealed class WallpaperClipContext : ApplicationContext
{
    private readonly IntPtr _target;
    private readonly string _logPath;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 120 };
    private string _lastRegionKey = "";

    public WallpaperClipContext(IntPtr target)
    {
        _target = target;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mineradio",
            "wallpaper-clip-debug.log");
        Log($"start target=0x{_target.ToInt64():X}");
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        if (_target == IntPtr.Zero || !Native.IsWindow(_target))
        {
            Log("exit target-missing");
            _timer.Stop();
            ExitThread();
            return;
        }

        var bounds = Native.DesktopBounds();
        Native.ConfigureDirectWallpaperWindow(_target, bounds);
        var rects = Native.VisibleTopLevelWindowRects(_target, bounds);
        var key = $"{bounds.Width}x{bounds.Height}|" + string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (key != _lastRegionKey)
        {
            Native.ApplyWindowRegionMinusRects(_target, bounds, rects);
            _lastRegionKey = key;
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch {}
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class WallpaperForm : Form
{
    private readonly string _statePath;
    private readonly bool _attachToDesktop;
    private readonly string _logPath;
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _attachTimer = new() { Interval = 1000 };
    private readonly StopwatchLite _clock = new();
    private readonly List<Particle> _particles = new();
    private WallpaperState _state = new();
    private Bitmap? _texture;
    private string _textureKey = "";
    private string _presentedFrameKey = "";
    private List<Rectangle> _cachedWindowMaskRects = new();
    private long _windowMaskCachedAt;
    private string _attachTarget = "";
    private IntPtr _attachParent = IntPtr.Zero;
    private Rectangle _attachBounds = Rectangle.Empty;
    private static readonly Color TransparentKeyColor = Color.FromArgb(1, 2, 3);

    public WallpaperForm(string statePath, bool attachToDesktop)
    {
        _statePath = statePath;
        _attachToDesktop = attachToDesktop;
        _logPath = Path.Combine(Path.GetDirectoryName(statePath) ?? AppContext.BaseDirectory, "native-wallpaper-debug.log");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = !attachToDesktop;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Bounds = Native.DesktopBounds();
        _frameTimer.Tick += (_, _) =>
        {
            if (!PresentCapturedStageFrame()) Invalidate();
        };
        _stateTimer.Tick += (_, _) => LoadState();
        _attachTimer.Tick += (_, _) => ReattachIfNeeded();
        Shown += (_, _) =>
        {
            Log($"shown attach={_attachToDesktop} hwnd=0x{Handle.ToInt64():X} bounds={Bounds.Width}x{Bounds.Height}");
            if (_attachToDesktop)
            {
                ReattachIfNeeded(true);
            }
            else
            {
                TopMost = true;
            }
            LoadState();
            EnsureParticles();
            _clock.Start();
            _stateTimer.Start();
            _frameTimer.Start();
            if (_attachToDesktop) _attachTimer.Start();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is diagnostic only; drawing must not depend on it.
        }
    }

    private void ReattachIfNeeded(bool force = false)
    {
        if (!_attachToDesktop || IsDisposed) return;
        var target = Native.ResolveDesktopAttachTarget();
        if (target.Parent == IntPtr.Zero)
        {
            if (force) Log($"attach-skip hwnd=0x{Handle.ToInt64():X} target=none");
            return;
        }

        var bounds = Native.DesktopBounds();
        var currentParent = Native.ParentOf(Handle);
        var parentChanged = currentParent != target.Parent || _attachParent != target.Parent;
        var targetChanged = !string.Equals(target.Key, _attachTarget, StringComparison.Ordinal);
        var boundsChanged = bounds.Width != _attachBounds.Width || bounds.Height != _attachBounds.Height;
        if (!force && !parentChanged && !targetChanged && !boundsChanged) return;

        Native.AttachToDesktopLayer(Handle, target, bounds);
        Bounds = bounds;
        _attachParent = target.Parent;
        _attachBounds = bounds;
        _attachTarget = target.Key;
        Log($"attached hwnd=0x{Handle.ToInt64():X} target={target.Key} bounds={bounds.Width}x{bounds.Height}");
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            _state.Enabled = root.GetBool("enabled", true);
            _state.Texture = root.GetString("texture") ?? root.GetString("cover") ?? "";
            _state.FramePath = root.GetString("framePath") ?? "";
            _state.ParticleMode = WallpaperParticleMode.Normalize(root.GetString("particleMode"));
            _state.Preset = Math.Clamp(root.GetInt("preset", 5), 0, 6);
            _state.Opacity = Math.Clamp(root.GetFloat("opacity", 1f), .35f, 1f);
            if (root.TryGetProperty("motion", out var motion))
            {
                _state.Bass = motion.GetFloat("bass", .10f);
                _state.Mid = motion.GetFloat("mid", .08f);
                _state.Treble = motion.GetFloat("treble", .08f);
                _state.Beat = motion.GetFloat("beat", .04f);
                _state.Energy = motion.GetFloat("energy", .10f);
            }
            if (root.TryGetProperty("lyric", out var lyric))
            {
                _state.LyricEnabled = lyric.GetBool("enabled", true);
                _state.LyricText = lyric.GetString("text") ?? "";
                _state.LyricProgress = Math.Clamp(lyric.GetFloat("progress", 0f), 0f, 1f);
            }
            if (root.TryGetProperty("colors", out var colors))
            {
                _state.Primary = ColorUtil.Parse(colors.GetString("primary"), Color.FromArgb(214, 248, 255));
                _state.Secondary = ColorUtil.Parse(colors.GetString("secondary"), Color.FromArgb(156, 255, 223));
                _state.Highlight = ColorUtil.Parse(colors.GetString("highlight"), Color.FromArgb(255, 240, 184));
                _state.Glow = ColorUtil.Parse(colors.GetString("glow"), Color.FromArgb(156, 255, 223));
            }
            LoadTextureIfNeeded();
        }
        catch
        {
            // Keep the last good state; malformed writes are possible while Electron is replacing the file.
        }
    }

    private List<Rectangle> CurrentWindowMaskRects(Rectangle bounds)
    {
        var now = Environment.TickCount64;
        if (now - _windowMaskCachedAt > 180)
        {
            _cachedWindowMaskRects = Native.VisibleTopLevelWindowRects(Handle, bounds);
            _windowMaskCachedAt = now;
        }
        return _cachedWindowMaskRects;
    }

    private bool PresentCapturedStageFrame()
    {
        var rawPath = _state.FramePath ?? "";
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        var path = rawPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(rawPath).LocalPath
            : rawPath;
        path = ResolveLatestStageFramePath(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;

        try
        {
            var info = new FileInfo(path);
            var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            if (key == _presentedFrameKey) return true;
            var bounds = Native.DesktopBounds();
            if (string.Equals(Path.GetExtension(path), ".mrf", StringComparison.OrdinalIgnoreCase))
            {
                return PresentRawStageFrame(path, key, bounds);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var src = new Bitmap(stream);
            using var frame = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(frame))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, frame.Width, frame.Height));
                using var clear = new SolidBrush(Color.Transparent);
                foreach (var windowRect in CurrentWindowMaskRects(bounds))
                {
                    var local = new Rectangle(
                        windowRect.X - bounds.X,
                        windowRect.Y - bounds.Y,
                        windowRect.Width,
                        windowRect.Height);
                    g.FillRectangle(clear, local);
                }
            }

            if (bounds.Width != Width || bounds.Height != Height) Bounds = bounds;
            if (Native.UpdateLayeredWindowFromBitmap(Handle, frame, bounds))
            {
                _presentedFrameKey = key;
                return true;
            }

            Log($"stage-frame-present-failed path={path}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"stage-frame-error {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static string ResolveLatestStageFramePath(string basePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(basePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return File.Exists(basePath) ? basePath : "";
            var stem = Path.GetFileNameWithoutExtension(basePath);
            var ext = Path.GetExtension(basePath);
            var latest = new FileInfo(basePath);
            var hasLatest = latest.Exists;
            foreach (var candidate in Directory.EnumerateFiles(dir, $"{stem}.*{ext}"))
            {
                if (candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(candidate);
                if (!info.Exists) continue;
                if (!hasLatest || info.LastWriteTimeUtc > latest.LastWriteTimeUtc)
                {
                    latest = info;
                    hasLatest = true;
                }
            }
            return hasLatest ? latest.FullName : "";
        }
        catch
        {
            return File.Exists(basePath) ? basePath : "";
        }
    }

    private bool PresentRawStageFrame(string path, string key, Rectangle bounds)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > int.MaxValue) return true;
            bytes = new byte[stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset < bytes.Length) return true;
        }
        if (bytes.Length < 16) return true;
        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'R' || bytes[2] != (byte)'F' || bytes[3] != (byte)'1') return false;
        var width = BitConverter.ToInt32(bytes, 4);
        var height = BitConverter.ToInt32(bytes, 8);
        var stride = BitConverter.ToInt32(bytes, 12);
        if (width <= 0 || height <= 0 || stride < width * 4) return true;
        var required = 16L + (long)stride * height;
        if (bytes.Length < required) return true;

        using var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rows = Math.Min(height, data.Height);
            for (var y = 0; y < rows; y++)
            {
                Marshal.Copy(bytes, 16 + y * stride, data.Scan0 + y * data.Stride, width * 4);
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        using (var g = Graphics.FromImage(frame))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.Transparent);
            foreach (var windowRect in CurrentWindowMaskRects(bounds))
            {
                var local = new Rectangle(
                    windowRect.X - bounds.X,
                    windowRect.Y - bounds.Y,
                    windowRect.Width,
                    windowRect.Height);
                g.FillRectangle(clear, local);
            }
        }

        if (bounds.Width != Width || bounds.Height != Height) Bounds = bounds;
        if (Native.UpdateLayeredWindowFromBitmap(Handle, frame, bounds))
        {
            _presentedFrameKey = key;
            return true;
        }

        Log($"stage-raw-frame-present-failed path={path}");
        return false;
    }

    private void LoadTextureIfNeeded()
    {
        var key = _state.Texture ?? "";
        if (key == _textureKey) return;
        _textureKey = key;
        _texture?.Dispose();
        _texture = null;
        try
        {
            var path = key.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(key).LocalPath
                : key;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using var src = new Bitmap(path);
                _texture = new Bitmap(src);
                Log($"texture-ok path={path} size={_texture.Width}x{_texture.Height}");
            }
            else if (!string.IsNullOrWhiteSpace(path))
            {
                Log($"texture-missing path={path}");
            }
        }
        catch (Exception ex)
        {
            _texture = null;
            Log($"texture-error {ex.GetType().Name}: {ex.Message}");
        }
        EnsureParticles(true);
    }

    private void EnsureParticles(bool rebuild = false)
    {
        if (!rebuild && _particles.Count > 0) return;
        _particles.Clear();
        var rnd = new Random(431960);
        var area = Math.Max(1, Bounds.Width * Bounds.Height);
        var count = Math.Clamp(area / 360, 6800, 14000);
        for (var i = 0; i < count; i++)
        {
            var u = (float)rnd.NextDouble();
            var v = (float)rnd.NextDouble();
            _particles.Add(new Particle
            {
                U = u,
                V = v,
                Rand = (float)rnd.NextDouble(),
                Size = 1.0f + (float)rnd.NextDouble() * 2.4f,
                Color = SampleColor(u, v)
            });
        }
    }

    private Color SampleColor(float u, float v)
    {
        if (_texture == null) return ColorUtil.Mix(_state.Secondary, _state.Highlight, u);
        var x = Math.Clamp((int)(u * (_texture.Width - 1)), 0, _texture.Width - 1);
        var y = Math.Clamp((int)(v * (_texture.Height - 1)), 0, _texture.Height - 1);
        return _texture.GetPixel(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        var rect = ClientRectangle;
        g.Clear(TransparentKeyColor);
        if (_state.ParticleMode != WallpaperParticleMode.TextureMouse) DrawPresetParticles(g, rect);
        if (_state.ParticleMode != WallpaperParticleMode.Ambient) DrawTextureGridMouseParticles(g, rect);
        DrawLyric(g, rect);
    }

    private void DrawBackground(Graphics g, Rectangle rect)
    {
        if (_texture != null)
        {
            var src = CoverSourceRect(_texture.Width, _texture.Height, rect.Width, rect.Height);
            g.DrawImage(_texture, rect, src, GraphicsUnit.Pixel);
        }
        else
        {
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.FromArgb(8, 10, 13), Color.FromArgb(18, 29, 32), 35f);
            g.FillRectangle(bg, rect);
        }
        using var veil = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
        g.FillRectangle(veil, rect);
    }

    private static Rectangle CoverSourceRect(int iw, int ih, int ow, int oh)
    {
        var imageRatio = iw / (float)ih;
        var outRatio = ow / (float)oh;
        if (imageRatio > outRatio)
        {
            var width = (int)(ih * outRatio);
            return new Rectangle((iw - width) / 2, 0, width, ih);
        }
        var height = (int)(iw / outRatio);
        return new Rectangle(0, (ih - height) / 2, iw, height);
    }

    private void DrawPresetParticles(Graphics g, Rectangle rect)
    {
        switch (_state.Preset)
        {
            case 0:
                DrawEmilyCoverParticles(g, rect);
                break;
            case 1:
                DrawTunnelParticles(g, rect);
                break;
            case 2:
                DrawOrbitParticles(g, rect);
                break;
            case 3:
                break;
            case 4:
                DrawVinylParticles(g, rect);
                break;
            case 6:
                DrawRequiemParticles(g, rect);
                break;
            case 5:
            default:
                DrawStarRiverParticles(g, rect);
                break;
        }
    }

    private void DrawStarRiverParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var mid = Math.Clamp(_state.Mid, 0f, 1.2f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        foreach (var p in _particles)
        {
            var dx = p.U - .5f;
            var dy = p.V - .5f;
            var angle = t * (.16f + p.Rand * .34f) + p.Rand * 6.28318f;
            var wave = (float)Math.Sin(t * (1.4f + p.Rand) + p.U * 10f + p.V * 6f);
            var expansion = 1f + bass * .075f + beat * .045f;
            var x = rect.Width * .5f + dx * rect.Width * expansion
                + (float)Math.Cos(angle) * (22f + beat * 54f) * (.35f + p.Rand);
            var y = rect.Height * .5f + dy * rect.Height * expansion
                + wave * (18f + mid * 42f);
            x = Wrap(x, rect.Width);
            y = Wrap(y, rect.Height);
            var alpha = (int)Math.Clamp((118 + beat * 105 + bass * 48) * _state.Opacity, 90, 238);
            var accent = ColorUtil.Mix(_state.Secondary, _state.Highlight, p.Rand);
            var col = ColorUtil.Mix(ColorUtil.Boost(p.Color, .64f + beat * .22f), accent, .52f + beat * .18f);
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = p.Size + 2.6f + beat * 3.8f + bass * 2.2f;
            g.FillEllipse(brush, x, y, size, size);
        }
    }

    private void DrawEmilyCoverParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var mid = Math.Clamp(_state.Mid, 0f, 1.2f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var coverW = Math.Min(rect.Width * .72f, rect.Height * 1.18f);
        var coverH = coverW * .72f;
        var cx = rect.Width * .50f;
        var cy = rect.Height * .50f;
        foreach (var p in _particles)
        {
            var dx = p.U - .5f;
            var dy = p.V - .5f;
            var wave = MathF.Sin(t * (1.5f + p.Rand) + p.U * 14f + p.V * 9f);
            var scatter = (18f + bass * 78f + beat * 46f) * (p.Rand - .5f);
            var x = cx + dx * coverW + scatter + MathF.Sin(t * .42f + p.Rand * 8f) * (8f + mid * 20f);
            var y = cy + dy * coverH + wave * (10f + bass * 34f);
            if (x < -8 || x > rect.Width + 8 || y < -8 || y > rect.Height + 8) continue;
            var col = ColorUtil.Mix(ColorUtil.Boost(p.Color, .28f + beat * .22f), _state.Highlight, .16f + beat * .14f);
            var alpha = (int)Math.Clamp((82 + beat * 96 + bass * 36) * _state.Opacity, 48, 230);
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = p.Size + 1.8f + beat * 4.2f + bass * 1.8f;
            g.FillEllipse(brush, x, y, size, size);
        }
    }

    private void DrawTunnelParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var mid = Math.Clamp(_state.Mid, 0f, 1.2f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var cx = rect.Width * .52f;
        var cy = rect.Height * .50f;
        var maxR = Math.Min(rect.Width, rect.Height) * (.58f + bass * .08f);
        foreach (var p in _particles)
        {
            var depth = (p.V + t * (.045f + bass * .030f)) % 1f;
            var angle = p.U * MathF.PI * 2f + t * (.32f + beat * .18f);
            var r = maxR * MathF.Pow(depth, 1.55f);
            var spiral = MathF.Sin(depth * 18f + t * 1.4f) * (20f + mid * 42f);
            var x = cx + MathF.Cos(angle) * (r + spiral);
            var y = cy + MathF.Sin(angle) * (r * .62f + spiral * .36f);
            var fall = Math.Clamp(depth, 0f, 1f);
            var col = ColorUtil.Mix(_state.Secondary, ColorUtil.Boost(p.Color, .42f), fall);
            var alpha = (int)Math.Clamp((36 + fall * 150 + beat * 48) * _state.Opacity, 20, 220);
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = 1.2f + fall * 4.8f + beat * 3.2f;
            g.FillEllipse(brush, x, y, size, size);
        }
    }

    private void DrawOrbitParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var treble = Math.Clamp(_state.Treble, 0f, 1.2f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var cx = rect.Width * .58f;
        var cy = rect.Height * .50f;
        var radius = Math.Min(rect.Width, rect.Height) * (.28f + bass * .035f);
        foreach (var p in _particles)
        {
            var theta = p.U * MathF.PI * 2f + t * (.16f + beat * .08f);
            var phi = (p.V - .5f) * MathF.PI;
            var ring = 1f + MathF.Sin(theta * 3f + t + p.Rand * 4f) * (.035f + treble * .05f);
            var x3 = MathF.Cos(phi) * MathF.Cos(theta) * radius * ring;
            var y3 = MathF.Sin(phi) * radius * .86f * ring;
            var z3 = MathF.Cos(phi) * MathF.Sin(theta) * radius;
            var scale = 1.08f / (1.08f + z3 / Math.Max(1f, radius) * .42f);
            var x = cx + x3 * scale;
            var y = cy + y3 * scale;
            var edge = .35f + Math.Abs(z3 / Math.Max(1f, radius)) * .65f;
            var col = ColorUtil.Mix(ColorUtil.Boost(p.Color, .36f), _state.Highlight, .18f + edge * .18f);
            var alpha = (int)Math.Clamp((58 + edge * 120 + beat * 50) * _state.Opacity, 34, 230);
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = (p.Size + 1.4f + beat * 3.0f) * scale;
            g.FillEllipse(brush, x, y, size, size);
        }
    }

    private void DrawVinylParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var cx = rect.Width * .70f;
        var cy = rect.Height * .52f;
        var maxR = Math.Min(rect.Width, rect.Height) * (.38f + bass * .035f);
        foreach (var p in _particles)
        {
            var ring = MathF.Sqrt(p.V);
            var angle = p.U * MathF.PI * 2f + t * (.55f + ring * .20f);
            var groove = MathF.Sin(ring * 92f + t * 3.0f) * (1.5f + beat * 4.0f);
            var r = maxR * (.16f + ring * .84f) + groove;
            var x = cx + MathF.Cos(angle) * r;
            var y = cy + MathF.Sin(angle) * r * .72f;
            var rim = ring > .88f ? 1f : 0f;
            var col = rim > 0 ? ColorUtil.Mix(_state.Highlight, Color.White, .36f) : ColorUtil.Mix(ColorUtil.Boost(p.Color, .22f), _state.Secondary, .22f);
            var alpha = (int)Math.Clamp((48 + ring * 92 + rim * 70 + beat * 48) * _state.Opacity, 24, 235);
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = 1.1f + rim * 2.4f + beat * 2.8f + p.Size * .55f;
            g.FillEllipse(brush, x, y, size, size);
        }
        using var center = new SolidBrush(Color.FromArgb(180, ColorUtil.Mix(_state.Highlight, Color.White, .55f)));
        g.FillEllipse(center, cx - maxR * .10f, cy - maxR * .10f, maxR * .20f, maxR * .20f);
    }

    private void DrawRequiemParticles(Graphics g, Rectangle rect)
    {
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var cx = rect.Width * .64f;
        var cy = rect.Height * .48f;
        var scale = Math.Min(rect.Width, rect.Height) * (.38f + bass * .025f);
        foreach (var p in _particles)
        {
            var x = (p.U - .5f) * 2f;
            var y = (p.V - .5f) * 2f;
            var skull = MathF.Exp(-(x * x * 1.4f + (y + .16f) * (y + .16f) * 2.1f));
            var jaw = MathF.Exp(-(x * x * 2.8f + (y - .56f) * (y - .56f) * 9.0f));
            var eyes = MathF.Exp(-((x - .28f) * (x - .28f) * 40f + (y + .24f) * (y + .24f) * 36f))
                + MathF.Exp(-((x + .28f) * (x + .28f) * 40f + (y + .24f) * (y + .24f) * 36f));
            var mask = Math.Clamp(skull + jaw * .65f - eyes * .9f, 0f, 1f);
            if (mask < .08f && p.Rand > .10f) continue;
            var drift = MathF.Sin(t * (1.1f + p.Rand) + p.Rand * 7f) * (8f + beat * 25f);
            var px = cx + x * scale * (.58f + mask * .12f) + drift;
            var py = cy + y * scale * .72f + MathF.Cos(t * .8f + p.Rand * 8f) * (6f + bass * 24f);
            var col = ColorUtil.Mix(Color.FromArgb(220, 225, 230), Color.FromArgb(80, 110, 255), .22f + p.Rand * .26f);
            if (p.Rand > .86f) col = ColorUtil.Mix(col, Color.FromArgb(170, 0, 24), .72f);
            var alpha = (int)Math.Clamp((30 + mask * 190 + beat * 42) * _state.Opacity, 0, 238);
            if (alpha <= 4) continue;
            using var brush = new SolidBrush(Color.FromArgb(alpha, col));
            var size = 1.1f + mask * 4.2f + beat * 2.6f + p.Size * .45f;
            g.FillEllipse(brush, px, py, size, size);
        }
    }

    private void DrawTextureGridMouseParticles(Graphics g, Rectangle rect)
    {
        if (_texture == null || rect.Width <= 0 || rect.Height <= 0) return;
        var t = _clock.ElapsedSeconds;
        var bass = Math.Clamp(_state.Bass, 0f, 1.3f);
        var mid = Math.Clamp(_state.Mid, 0f, 1.2f);
        var beat = Math.Clamp(Math.Max(_state.Beat, _state.Energy * .55f), 0f, 1.5f);
        var cursor = Native.CursorPosition();
        var mx = Math.Clamp(cursor.X, 0, rect.Width);
        var my = Math.Clamp(cursor.Y, 0, rect.Height);
        var radius = Math.Min(rect.Width, rect.Height) * (.18f + beat * .055f + bass * .035f);
        radius = Math.Clamp(radius, 210f, Math.Min(rect.Width, rect.Height) * .46f);
        var step = Math.Clamp((int)(18 - beat * 4 - bass * 2), 11, 18);
        var left = Math.Max(0, (int)(mx - radius - step));
        var right = Math.Min(rect.Width, (int)(mx + radius + step));
        var top = Math.Max(0, (int)(my - radius - step));
        var bottom = Math.Min(rect.Height, (int)(my + radius + step));
        var radiusSq = radius * radius;

        for (var y = top; y <= bottom; y += step)
        {
            for (var x = left; x <= right; x += step)
            {
                var u = x / (float)Math.Max(1, rect.Width);
                var v = y / (float)Math.Max(1, rect.Height);
                var rnd = Hash01((int)(u * 10007), (int)(v * 10009));
                var dx = x - mx;
                var dy = y - my;
                var d2 = dx * dx + dy * dy;
                if (d2 > radiusSq) continue;
                var falloff = 1f - MathF.Sqrt(d2) / radius;
                var breathe = MathF.Sin(t * (1.7f + rnd) + u * 18f + v * 12f);
                var push = (36f + beat * 92f + bass * 45f) * falloff;
                var angle = MathF.Atan2(dy, dx) + breathe * .36f;
                var px = x + MathF.Cos(angle) * push * (.22f + rnd * .78f);
                var py = y + MathF.Sin(angle) * push * (.18f + (1f - rnd) * .54f) + breathe * (4f + mid * 16f);
                var sample = SampleColor(u, v);
                var accent = ColorUtil.Mix(_state.Highlight, _state.Secondary, rnd);
                var col = ColorUtil.Mix(ColorUtil.Boost(sample, .35f + falloff * .35f), accent, .24f + falloff * .38f);
                var alpha = (int)Math.Clamp((46 + falloff * 188 + beat * 38) * _state.Opacity, 0, 245);
                if (alpha <= 4) continue;
                var size = 1.8f + falloff * (4.2f + beat * 5.2f) + rnd * 2.2f;
                using var brush = new SolidBrush(Color.FromArgb(alpha, col));
                g.FillEllipse(brush, px, py, size, size);
            }
        }
    }

    private static float Wrap(float value, float max)
    {
        if (max <= 0) return value;
        value %= max;
        return value < 0 ? value + max : value;
    }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            var n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            return ((n ^ (n >> 16)) & 0x00FFFFFF) / 16777215f;
        }
    }


    private void DrawLyric(Graphics g, Rectangle rect)
    {
        if (!_state.LyricEnabled || string.IsNullOrWhiteSpace(_state.LyricText)) return;
        var text = _state.LyricText.Trim();
        using var font = new Font("Microsoft YaHei UI", Math.Max(32, rect.Width / 34f), FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var x = _attachToDesktop ? rect.Width * .76f - size.Width / 2f : (rect.Width - size.Width) / 2f;
        var y = _attachToDesktop ? Math.Max(84, rect.Height * .085f) : rect.Height - Math.Max(178, rect.Height * .16f);
        using var shadow = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
        using var baseBrush = new SolidBrush(Color.FromArgb(214, Color.White));
        g.DrawString(text, font, shadow, x + 3, y + 4);
        g.DrawString(text, font, baseBrush, x, y);
        var hiWidth = size.Width * Math.Clamp(_state.LyricProgress, 0f, 1f);
        if (hiWidth > 1)
        {
            var oldClip = g.Clip;
            g.SetClip(new RectangleF(x, y - 6, hiWidth, size.Height + 12));
            using var hi = new SolidBrush(Color.FromArgb(244, _state.Highlight));
            g.DrawString(text, font, hi, x, y);
            g.Clip = oldClip;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Dispose();
            _stateTimer.Dispose();
            _attachTimer.Dispose();
            _texture?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class WallpaperState
{
    public bool Enabled = true;
    public string Texture = "";
    public string FramePath = "";
    public string ParticleMode = WallpaperParticleMode.Both;
    public int Preset = 5;
    public float Opacity = 1f;
    public float Bass = .10f;
    public float Mid = .08f;
    public float Treble = .08f;
    public float Beat = .04f;
    public float Energy = .10f;
    public bool LyricEnabled = true;
    public string LyricText = "Mineradio";
    public float LyricProgress = 0f;
    public Color Primary = Color.FromArgb(214, 248, 255);
    public Color Secondary = Color.FromArgb(156, 255, 223);
    public Color Highlight = Color.FromArgb(255, 240, 184);
    public Color Glow = Color.FromArgb(156, 255, 223);
}

internal static class WallpaperParticleMode
{
    public const string Both = "both";
    public const string Ambient = "ambient";
    public const string TextureMouse = "textureMouse";

    public static string Normalize(string? value)
    {
        return value switch
        {
            Ambient => Ambient,
            TextureMouse => TextureMouse,
            _ => Both
        };
    }
}

internal struct Particle
{
    public float U;
    public float V;
    public float Rand;
    public float Size;
    public Color Color;
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
        return el.TryGetProperty(name, out var v) && v.TryGetSingle(out var n) ? n : fallback;
    }

    public static int GetInt(this JsonElement el, string name, int fallback)
    {
        return el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
    }
}

internal static class ColorUtil
{
    public static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static Color Boost(Color c, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            Math.Clamp((int)(c.R + (255 - c.R) * amount), 0, 255),
            Math.Clamp((int)(c.G + (255 - c.G) * amount), 0, 255),
            Math.Clamp((int)(c.B + (255 - c.B) * amount), 0, 255));
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
    public DesktopAttachTarget(IntPtr parent, IntPtr insertAfter, string key)
    {
        Parent = parent;
        InsertAfter = insertAfter;
        Key = key;
    }

    public IntPtr Parent { get; }
    public IntPtr InsertAfter { get; }
    public string Key { get; }
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
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int ULW_ALPHA = 0x00000002;
    private const int RGN_DIFF = 4;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx; public int Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowProc proc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc proc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT point);

    public static Rectangle DesktopBounds()
    {
        var progman = FindProgman();
        if (progman != IntPtr.Zero && GetClientRect(progman, out var rect))
        {
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            return new Rectangle(0, 0, width, height);
        }
        return SystemInformation.VirtualScreen;
    }

    public static Point CursorPosition()
    {
        if (GetCursorPos(out var point)) return new Point(point.X, point.Y);
        return new Point(SystemInformation.VirtualScreen.Width / 2, SystemInformation.VirtualScreen.Height / 2);
    }

    public static IntPtr ParentOf(IntPtr hwnd) => GetParent(hwnd);

    public static void ConfigureDirectWallpaperWindow(IntPtr hwnd, Rectangle bounds)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_CHILD) | WS_POPUP | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    public static void ApplyWindowRegionMinusRects(IntPtr hwnd, Rectangle bounds, IReadOnlyList<Rectangle> cutouts)
    {
        var region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
        if (region == IntPtr.Zero) return;
        foreach (var cutout in cutouts)
        {
            var local = new Rectangle(cutout.X - bounds.X, cutout.Y - bounds.Y, cutout.Width, cutout.Height);
            local.Intersect(new Rectangle(0, 0, bounds.Width, bounds.Height));
            if (local.Width <= 0 || local.Height <= 0) continue;
            var cutRegion = CreateRectRgn(local.Left, local.Top, local.Right, local.Bottom);
            if (cutRegion == IntPtr.Zero) continue;
            CombineRgn(region, region, cutRegion, RGN_DIFF);
            DeleteObject(cutRegion);
        }
        SetWindowRgn(hwnd, region, true);
        // SetWindowRgn takes ownership of a successful region handle.
    }

    public static List<Rectangle> VisibleTopLevelWindowRects(IntPtr excludeHwnd, Rectangle desktopBounds)
    {
        var rects = new List<Rectangle>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || hwnd == excludeHwnd) return true;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var cls = ClassName(hwnd);
            if (cls is "Progman"
                or "WorkerW"
                or "Windows.UI.Core.CoreWindow"
                or "CEF-OSC-WIDGET"
                or "WPEImageWallpaper"
                or "WPEUI"
                or "GameViewerPrivateWindowClass") return true;
            if (!GetWindowRect(hwnd, out var nativeRect)) return true;
            var rect = Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            if (rect.Width < 8 || rect.Height < 8) return true;
            rect.Intersect(desktopBounds);
            if (rect.Width > 0 && rect.Height > 0) rects.Add(rect);
            return true;
        }, IntPtr.Zero);
        return rects;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var count = GetClassName(hwnd, sb, sb.Capacity);
        return count > 0 ? sb.ToString(0, count) : "";
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr hwnd, Bitmap bitmap, Rectangle bounds)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        var memoryDc = IntPtr.Zero;
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return false;
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, hBitmap);
            var dst = new POINT { X = bounds.X, Y = bounds.Y };
            var size = new SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            return UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memoryDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static DesktopAttachTarget ResolveDesktopAttachTarget()
    {
        var progman = FindProgman();
        if (progman == IntPtr.Zero) return new DesktopAttachTarget(IntPtr.Zero, IntPtr.Zero, "none");
        var iconHost = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        return new DesktopAttachTarget(progman, iconHost, $"Progman:0x{progman.ToInt64():X}");
    }

    public static void AttachToDesktopLayer(IntPtr hwnd, DesktopAttachTarget target, Rectangle bounds)
    {
        if (target.Parent == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        SetParent(hwnd, target.Parent);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        if (target.InsertAfter != IntPtr.Zero)
        {
            SetWindowPos(hwnd, target.InsertAfter, 0, 0, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }

    private static IntPtr FindProgman()
    {
        var progman = FindWindow("Progman", "Program Manager");
        return progman != IntPtr.Zero ? progman : FindWindow("Progman", null);
    }

    private static IntPtr FindWallpaperWorker(IntPtr progman)
    {
        var worker = IntPtr.Zero;
        while ((worker = FindWindowEx(progman, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            var wpe = FindWindowEx(worker, IntPtr.Zero, "WPEImageWallpaper", null);
            if (wpe != IntPtr.Zero) return worker;
        }

        var found = IntPtr.Zero;
        EnumChildWindows(progman, (hwnd, _) =>
        {
            var wpe = FindWindowEx(hwnd, IntPtr.Zero, "WPEImageWallpaper", null);
            if (wpe == IntPtr.Zero) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
