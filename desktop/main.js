const { app, BrowserWindow, ipcMain, shell, screen, session, globalShortcut, dialog, desktopCapturer, Tray, nativeImage } = require('electron');
const net = require('net');
const http = require('http');
const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const { fileURLToPath, pathToFileURL } = require('url');
const { execFile, execFileSync, spawn } = require('child_process');

let mainWindow = null;
let localServer = null;
let mainServerPort = 0;
let wallpaperEngineBridgeServer = null;
let wallpaperEngineBridgePort = 19777;
let desktopLyricsWindow = null;
let desktopLyricsState = {};
let desktopLyricsUserBounds = null;
let desktopLyricsProgrammaticMove = false;
let desktopLyricsPointerCapture = false;
let desktopLyricsMouseIgnored = null;
let desktopLyricsMousePoller = null;
let desktopLyricsMousePollerBuffer = '';
let desktopLyricsHotBounds = null;
let desktopLyricsLastMiddleAt = 0;
let desktopLyricsMiddleAdjustActive = false;
let desktopLyricsBoundsSaveTimer = null;
let desktopLyricsPendingState = null;
let desktopLyricsPendingBeatPayload = null;
let desktopLyricsUpdateTimer = null;
let desktopLyricsLastUpdateAt = 0;
const DESKTOP_LYRICS_UPDATE_INTERVAL_MS = 32;
const DESKTOP_LYRICS_DEFAULT_WIDTH_RATIO = 2002 / 2560;
const DESKTOP_LYRICS_DEFAULT_HEIGHT_RATIO = 246 / 1440;
const DESKTOP_LYRICS_REFERENCE_DISPLAY_BOUNDS = Object.freeze({ x: 0, y: 0, width: 2560, height: 1440 });
let wallpaperWindow = null;
let wallpaperState = {};
let wallpaperWindowLoadReady = false;
let wallpaperWindowLoadPromise = null;
let wallpaperWindowLoadResolve = null;
let wallpaperWindowLoadReject = null;
let wallpaperWindowLoadTimer = null;
let wallpaperAttachInFlight = false;
let wallpaperAttachPending = false;
let wallpaperAttachedHwnd = '';
let wallpaperSuspendedForMainWindow = false;
let wallpaperAttachRetryTimer = null;
let wallpaperFrameCaptureTimer = null;
let wallpaperFrameCaptureInFlight = false;
let wallpaperFrameCaptureLastLogAt = 0;
let wallpaperFrameCleanupLastAt = 0;
let nativeWallpaperProcess = null;
let nativeWallpaperWindowShim = null;
let nativeWallpaperPipeClient = null;
let nativeWallpaperPipeConnected = false;
let nativeWallpaperPipeConnecting = false;
let nativeWallpaperPipeRetryTimer = null;
let nativeWallpaperRestartTimer = null;
let nativeWallpaperPipeBackpressure = false;
let nativeWallpaperPipeLastErrorAt = 0;
let nativeWallpaperFallbackWriteLastAt = 0;
let nativeWallpaperMotionDebugLastAt = 0;
let wallpaperClipProcess = null;
let wallpaperClipProcessHwnd = '';
let wallpaperMousePoller = null;
let wallpaperLastPointerKey = '';
let wallpaperInputHookProcess = null;
let wallpaperInputHookBuffer = '';
let wallpaperPrimaryDown = false;
let wallpaperRendererConsoleLast = new Map();
let wallpaperEngineConfigWatchers = new Map();
let wallpaperEngineConfigWatchDebounce = null;
let wallpaperEngineConfigWatchSkipLastAt = 0;
let wallpaperEnginePlayerFollowWatchEnabled = false;
let wallpaperEngineBridgeEventClients = new Set();
let wallpaperEngineLastSelectedKey = '';
let wallpaperEngineChangeInFlight = false;
let htmlFullscreenActive = false;
let windowFullscreenActive = false;
let mainWindowStateTimer = null;
let mainWindowDragSession = null;
let mainWindowResizeSession = null;
let mainWindowSnapRestoreBounds = null;
let mainWindowPreFullscreenBounds = null;
let mainWindowStateSaveTimer = null;
let isQuitting = false;
let tray = null;
let trayMenuWindow = null;
let trayPlayerState = {
  title: '',
  artist: '',
  cover: '',
  playing: false,
  liked: false,
  desktopLyrics: false,
  wallpaperMode: false,
  volume: 1,
  thumbnailClip: null,
};
let taskbarThumbIcons = null;
const registeredGlobalHotkeys = new Map();

const WINDOWED_ASPECT = 16 / 9;
const WINDOWED_SCALE = 3 / 4;
const WINDOWED_MARGIN = 32;
const MIN_WINDOWED_WIDTH = 960;
const MIN_WINDOWED_HEIGHT = 540;
const WINDOW_SNAP_EDGE = 18;
const APP_NAME = 'Mineradio';
const APP_USER_MODEL_ID = 'com.mineradio.desktop';
const TRAY_MENU_WIDTH = 340;
const TRAY_MENU_DEFAULT_HEIGHT = 426;
const TRAY_MENU_MIN_HEIGHT = 380;
const TRAY_MENU_MAX_HEIGHT = 450;
let trayMenuHeight = TRAY_MENU_DEFAULT_HEIGHT;
const RESTORE_WALLPAPER_ON_STARTUP = false;
const WALLPAPER_INPUT_HOOK_ENABLED = false;
const WALLPAPER_POINTER_SYNC_ENABLED = false;
const WALLPAPER_CLIP_CONTROLLER_ENABLED = false;
const WALLPAPER_ENGINE_AUTO_CAPTURE_ENABLED = false;
const APP_ICON_ICO = path.join(__dirname, '..', 'build', 'icon.ico');
const WALLPAPER_ENGINE_STEAM_URL = 'steam://rungameid/431960';
const WALLPAPER_TEXTURE_IMAGE_EXTS = new Set(['.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.avif']);
const WALLPAPER_PREVIEW_VIDEO_EXTS = new Set(['.mp4', '.webm', '.mov', '.m4v']);
const WALLPAPER_PREVIEW_WEB_EXTS = new Set(['.html', '.htm']);
const LOCAL_AUDIO_EXTS = new Set(['.mp3', '.flac', '.wav', '.ogg', '.m4a', '.aac', '.opus']);
const WALLPAPER_ENGINE_APP_ID = '431960';
const MINERADIO_WE_BRIDGE_PROJECT_NAME = 'mineradio_we_bridge';
const MINERADIO_WE_OVERLAY_PROJECT_NAME = 'mineradio_we_overlay';
const USE_NATIVE_WALLPAPER = false;
const USE_NATIVE_D3D11_WALLPAPER = true;
const USE_NATIVE_WALLPAPER_FRAME_PRESENTER = false;
const USE_DIRECT_ELECTRON_WALLPAPER = true;
const USE_WEBGL_SONIC_WALLPAPER = false;
const USE_WALLPAPER_ENGINE_WEB_BRIDGE = false;
const USE_DIRECT_WALLPAPER_WINDOW_CONFIGURE = false;
const DEFAULT_NATIVE_DESKTOP_MODE = 'desktop-layer';
const NETEASE_LOGIN_PARTITION = 'persist:mineradio-netease-login';
const NETEASE_LOGIN_URL = 'https://music.163.com/#/login';
const QQ_LOGIN_PARTITION = 'persist:mineradio-qqmusic-login';
const QQ_LOGIN_URL = 'https://y.qq.com/n/ryqq/profile';

function desktopSettingsPath() {
  return path.join(app.getPath('userData'), 'desktop-settings.json');
}

function wallpaperMotionDebugFlagPath() {
  return path.join(app.getPath('userData'), 'wallpaper-motion-debug.flag');
}

function readDesktopSettings() {
  try {
    const raw = JSON.parse(fs.readFileSync(desktopSettingsPath(), 'utf8'));
    return {
      closeToTray: raw.closeToTray !== false,
      openAtLogin: !!app.getLoginItemSettings().openAtLogin,
    };
  } catch (_) {
    return {
      closeToTray: true,
      openAtLogin: !!app.getLoginItemSettings().openAtLogin,
    };
  }
}

function writeDesktopSettings(patch = {}) {
  const current = readDesktopSettings();
  const next = {
    ...current,
    closeToTray: patch.closeToTray == null ? current.closeToTray : patch.closeToTray !== false,
  };
  fs.mkdirSync(path.dirname(desktopSettingsPath()), { recursive: true });
  fs.writeFileSync(desktopSettingsPath(), JSON.stringify({ closeToTray: next.closeToTray }, null, 2), 'utf8');
  if (patch.openAtLogin != null) {
    app.setLoginItemSettings({
      openAtLogin: !!patch.openAtLogin,
      path: process.execPath,
    });
  }
  return readDesktopSettings();
}

function desktopWindowStatePath() {
  return path.join(app.getPath('userData'), 'window-state.json');
}

function normalizeWindowBounds(bounds) {
  if (!bounds || typeof bounds !== 'object') return null;
  const x = Math.round(Number(bounds.x));
  const y = Math.round(Number(bounds.y));
  const width = Math.round(Number(bounds.width));
  const height = Math.round(Number(bounds.height));
  if (![x, y, width, height].every(Number.isFinite)) return null;
  if (width < 320 || height < 240) return null;
  return {
    x,
    y,
    width: Math.max(MIN_WINDOWED_WIDTH, width),
    height: Math.max(MIN_WINDOWED_HEIGHT, height),
  };
}

function clampWindowBoundsToDisplays(bounds) {
  const normalized = normalizeWindowBounds(bounds);
  if (!normalized) return null;
  const displays = screen.getAllDisplays();
  const hasIntersection = displays.some((display) => {
    const area = display.workArea || display.bounds;
    if (!area) return false;
    const ix = Math.max(0, Math.min(normalized.x + normalized.width, area.x + area.width) - Math.max(normalized.x, area.x));
    const iy = Math.max(0, Math.min(normalized.y + normalized.height, area.y + area.height) - Math.max(normalized.y, area.y));
    return ix >= Math.min(160, normalized.width * 0.25) && iy >= Math.min(120, normalized.height * 0.25);
  });
  if (hasIntersection) return normalized;
  const area = screen.getPrimaryDisplay().workArea;
  const width = Math.min(Math.max(MIN_WINDOWED_WIDTH, normalized.width), area.width);
  const height = Math.min(Math.max(MIN_WINDOWED_HEIGHT, normalized.height), area.height);
  return {
    x: Math.round(area.x + (area.width - width) / 2),
    y: Math.round(area.y + (area.height - height) / 2),
    width,
    height,
  };
}

function centerWindowBoundsOnDisplay(bounds, display) {
  const normalized = normalizeWindowBounds(bounds) || getWindowedBounds();
  const area = (display && display.workArea) || screen.getPrimaryDisplay().workArea;
  const width = Math.min(Math.max(MIN_WINDOWED_WIDTH, normalized.width), area.width);
  const height = Math.min(Math.max(MIN_WINDOWED_HEIGHT, normalized.height), area.height);
  return {
    x: Math.round(area.x + (area.width - width) / 2),
    y: Math.round(area.y + (area.height - height) / 2),
    width,
    height,
  };
}

function boundsFillDisplay(bounds, display) {
  const normalized = normalizeWindowBounds(bounds);
  if (!normalized) return false;
  const displayBounds = (display && display.bounds) || null;
  const workArea = (display && display.workArea) || displayBounds;
  const closeTo = (a, b, tolerance = 8) => Math.abs((Number(a) || 0) - (Number(b) || 0)) <= tolerance;
  return [displayBounds, workArea].filter(Boolean).some((area) => {
    return closeTo(normalized.x, area.x)
      && closeTo(normalized.y, area.y)
      && normalized.width >= area.width - 8
      && normalized.height >= area.height - 8;
  });
}

function nonFullscreenWindowBounds(bounds, win, display) {
  const targetDisplay = display || (win && !win.isDestroyed()
    ? screen.getDisplayMatching(win.getBounds())
    : screen.getPrimaryDisplay());
  if (boundsFillDisplay(bounds, targetDisplay)) return getWindowedBounds(win);
  return bounds;
}

function readDesktopWindowState() {
  try {
    const raw = JSON.parse(fs.readFileSync(desktopWindowStatePath(), 'utf8'));
    return {
      bounds: clampWindowBoundsToDisplays(raw.bounds || raw.normalBounds),
      normalBounds: clampWindowBoundsToDisplays(raw.normalBounds || raw.bounds),
      maximized: !!raw.maximized,
    };
  } catch (_) {
    return { bounds: null, normalBounds: null, maximized: false };
  }
}

function writeDesktopWindowStateFor(win) {
  if (!win || win.isDestroyed()) return;
  try {
    const current = readDesktopWindowState();
    const maximized = win.isMaximized();
    const rawBounds = (win.isFullScreen() || windowFullscreenActive || htmlFullscreenActive)
      ? (mainWindowPreFullscreenBounds || current.normalBounds || current.bounds || getWindowedBounds(win))
      : win.getBounds();
    const display = screen.getDisplayMatching(rawBounds || win.getBounds());
    const normalBounds = nonFullscreenWindowBounds(rawBounds, win, display);
    const bounds = maximized
      ? (current.normalBounds || clampWindowBoundsToDisplays(normalBounds))
      : clampWindowBoundsToDisplays(normalBounds);
    const payload = {
      bounds,
      normalBounds: bounds,
      maximized,
      updatedAt: Date.now(),
    };
    fs.mkdirSync(path.dirname(desktopWindowStatePath()), { recursive: true });
    fs.writeFileSync(desktopWindowStatePath(), JSON.stringify(payload, null, 2), 'utf8');
  } catch (e) {
    console.warn('Window state save failed:', e.message || e);
  }
}

function scheduleDesktopWindowStateSave(win, delay = 220) {
  if (!win || win.isDestroyed()) return;
  if (win.isMinimized() || win.isFullScreen()) return;
  if (mainWindowStateSaveTimer) clearTimeout(mainWindowStateSaveTimer);
  mainWindowStateSaveTimer = setTimeout(() => {
    mainWindowStateSaveTimer = null;
    writeDesktopWindowStateFor(win);
  }, delay);
}

function wallpaperDebugLog(event, details = {}) {
  try {
    const line = JSON.stringify({
      at: new Date().toISOString(),
      event,
      details,
    }) + '\n';
    fs.appendFileSync(path.join(app.getPath('userData'), 'wallpaper-debug.log'), line, 'utf8');
  } catch (_) {}
}

function nativeWallpaperStatePath() {
  return path.join(app.getPath('userData'), 'wallpaper-state.json');
}

function wallpaperEngineSyncHotPathDisabled() {
  return shouldUseNativeD3D11Wallpaper(wallpaperState);
}

function normalizeNativeDesktopMode(mode) {
  const value = String(mode || '').trim().toLowerCase();
  if (value === 'overlay' || value === 'desktop-overlay' || value === 'overlay-fallback') return 'overlay-fallback';
  if (value === 'disable' || value === 'disabled' || value === 'disable-desktop-layer') return 'disabled';
  return 'desktop-layer';
}

function nativeDesktopModeLaunchArg(mode) {
  const normalized = normalizeNativeDesktopMode(mode);
  if (normalized === 'overlay-fallback') return '--desktop-overlay';
  if (normalized === 'disabled') return '--disable-desktop-layer';
  return '--desktop-layer';
}

function wallpaperStageFramePath() {
  return path.join(app.getPath('userData'), 'wallpaper-stage-frame.mrf');
}

function wallpaperEngineBridgeState() {
  const state = normalizeWallpaperPayload(wallpaperState || {});
  const cover = bridgeAssetUrl(state.cover || '');
  const texture = bridgeAssetUrl(state.texture || state.cover || '');
  const playerVisible = !!(
    mainWindow
    && !mainWindow.isDestroyed()
    && mainWindow.isVisible()
    && !mainWindow.isMinimized()
  );
  return {
    ok: true,
    enabled: !!state.enabled,
    title: state.title || '',
    artist: state.artist || '',
    cover,
    texture,
    textureType: state.textureType || '',
    textureName: state.textureName || '',
    particleMode: state.particleMode || 'stage',
    playing: !!state.playing,
    paused: false,
    playerVisible,
    frameRate: normalizeWallpaperFrameRate(state.frameRate),
    throttleFps: normalizeWallpaperThrottleFps(state.throttleFps),
    preset: state.preset || 0,
    opacity: state.opacity == null ? 1 : state.opacity,
    lyric: state.lyric || {},
    motion: state.motion || {},
    colors: state.colors || {},
    visual: state.visual || {},
    at: Date.now(),
  };
}

function bridgeAssetUrl(src) {
  const raw = String(src || '').trim();
  if (!raw) return '';
  if (/^file:\/\//i.test(raw)) {
    return `http://127.0.0.1:${wallpaperEngineBridgePort}/asset?src=${encodeURIComponent(raw)}`;
  }
  return raw;
}

function bridgeMimeForPath(filePath) {
  const ext = path.extname(filePath || '').toLowerCase();
  if (ext === '.png') return 'image/png';
  if (ext === '.jpg' || ext === '.jpeg') return 'image/jpeg';
  if (ext === '.webp') return 'image/webp';
  if (ext === '.gif') return 'image/gif';
  if (ext === '.bmp') return 'image/bmp';
  if (ext === '.avif') return 'image/avif';
  if (ext === '.mp4' || ext === '.m4v') return 'video/mp4';
  if (ext === '.webm') return 'video/webm';
  if (ext === '.mov') return 'video/quicktime';
  if (ext === '.html' || ext === '.htm') return 'text/html; charset=utf-8';
  if (ext === '.js') return 'application/javascript; charset=utf-8';
  if (ext === '.css') return 'text/css; charset=utf-8';
  return 'application/octet-stream';
}

function filePathFromBridgeAsset(src) {
  try {
    const parsed = new URL(String(src || ''));
    if (parsed.protocol !== 'file:') return '';
    return decodeURIComponent(parsed.pathname || '').replace(/^\/([A-Za-z]:\/)/, '$1').replace(/\//g, path.sep);
  } catch (_) {
    return '';
  }
}

function writeBridgeJson(res, code, data) {
  res.writeHead(code, {
    'Content-Type': 'application/json; charset=utf-8',
    'Access-Control-Allow-Origin': '*',
    'Cache-Control': 'no-store',
    'Connection': 'close',
  });
  res.end(JSON.stringify(data));
}

function fetchBridgeText(url, timeoutMs = 2500) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, {
      headers: {
        'Cache-Control': 'no-store',
      },
      timeout: timeoutMs,
    }, (resp) => {
      if (resp.statusCode < 200 || resp.statusCode >= 300) {
        resp.resume();
        reject(new Error(`HTTP_${resp.statusCode}`));
        return;
      }
      resp.setEncoding('utf8');
      let body = '';
      resp.on('data', (chunk) => {
        body += chunk;
      });
      resp.on('end', () => resolve(body));
    });
    req.on('timeout', () => {
      req.destroy(new Error('STAGE_FETCH_TIMEOUT'));
    });
    req.on('error', reject);
  });
}

async function writeWallpaperEngineStage(res) {
  if (!mainServerPort) {
    res.writeHead(503, {
      'Content-Type': 'text/html; charset=utf-8',
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'no-store',
      'Connection': 'close',
    });
    res.end('<!doctype html><meta charset="utf-8"><body style="margin:0;background:transparent;color:#fff;font:14px sans-serif"><script>setTimeout(function(){location.reload()},800)</script></body>');
    return;
  }
  const stageUrl = overlayUrl('index.html?wallpaperStage=1&wallpaperEngineHost=1');
  try {
    let html = await fetchBridgeText(stageUrl);
    const base = `<base href="${overlayUrl('')}">`;
    if (/<head[^>]*>/i.test(html)) {
      html = html.replace(/<head[^>]*>/i, (tag) => `${tag}\n${base}`);
    } else {
      html = `${base}\n${html}`;
    }
    res.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'no-store',
      'Connection': 'close',
    });
    res.end(html);
  } catch (e) {
    wallpaperDebugLog('wallpaper-engine-stage-fetch-failed', {
      stageUrl,
      error: e.message || String(e),
    });
    res.writeHead(503, {
      'Content-Type': 'text/html; charset=utf-8',
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'no-store',
      'Connection': 'close',
    });
    res.end('<!doctype html><meta charset="utf-8"><body style="margin:0;background:transparent"></body>');
  }
}

function writeBridgeFile(req, res, filePath) {
  const stat = fs.statSync(filePath);
  const total = stat.size;
  const mime = bridgeMimeForPath(filePath);
  const range = String(req.headers.range || '').trim();
  if (range) {
    const match = /^bytes=(\d*)-(\d*)$/i.exec(range);
    if (!match) {
      res.writeHead(416, {
        'Content-Range': `bytes */${total}`,
        'Access-Control-Allow-Origin': '*',
        'Connection': 'close',
      });
      res.end();
      return;
    }
    let start = match[1] === '' ? 0 : Number(match[1]);
    let end = match[2] === '' ? total - 1 : Number(match[2]);
    if (match[1] === '' && match[2] !== '') {
      const suffix = Math.max(0, Number(match[2]) || 0);
      start = Math.max(0, total - suffix);
      end = total - 1;
    }
    if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end < start || start >= total) {
      res.writeHead(416, {
        'Content-Range': `bytes */${total}`,
        'Access-Control-Allow-Origin': '*',
        'Connection': 'close',
      });
      res.end();
      return;
    }
    end = Math.min(end, total - 1);
    res.writeHead(206, {
      'Content-Type': mime,
      'Content-Length': String(end - start + 1),
      'Content-Range': `bytes ${start}-${end}/${total}`,
      'Accept-Ranges': 'bytes',
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'no-store',
      'Connection': 'close',
    });
    if (req.method === 'HEAD') {
      res.end();
      return;
    }
    fs.createReadStream(filePath, { start, end })
      .on('error', () => {
        try { res.destroy(); } catch (_) {}
      })
      .pipe(res);
    return;
  }
  res.writeHead(200, {
    'Content-Type': mime,
    'Content-Length': String(total),
    'Accept-Ranges': 'bytes',
    'Access-Control-Allow-Origin': '*',
    'Cache-Control': 'no-store',
    'Connection': 'close',
  });
  if (req.method === 'HEAD') {
    res.end();
    return;
  }
  fs.createReadStream(filePath)
    .on('error', () => {
      try { res.destroy(); } catch (_) {}
    })
    .pipe(res);
}

function wallpaperEngineBridgeStateJson() {
  return JSON.stringify(wallpaperEngineBridgeState());
}

function writeBridgeSseState(res) {
  res.write(`event: state\ndata: ${wallpaperEngineBridgeStateJson()}\n\n`);
}

function broadcastWallpaperEngineBridgeState() {
  if (!wallpaperEngineBridgeEventClients.size) return;
  const data = `event: state\ndata: ${wallpaperEngineBridgeStateJson()}\n\n`;
  for (const res of Array.from(wallpaperEngineBridgeEventClients)) {
    try {
      res.write(data);
    } catch (_) {
      wallpaperEngineBridgeEventClients.delete(res);
      try { res.destroy(); } catch (_) {}
    }
  }
}

function startWallpaperEngineBridgeServer() {
  if (wallpaperEngineBridgeServer) return;
  wallpaperEngineBridgeServer = http.createServer((req, res) => {
    try {
      const url = new URL(req.url || '/', `http://127.0.0.1:${wallpaperEngineBridgePort}`);
      if (url.pathname === '/events') {
        res.writeHead(200, {
          'Content-Type': 'text/event-stream; charset=utf-8',
          'Access-Control-Allow-Origin': '*',
          'Cache-Control': 'no-store, no-transform',
          'Connection': 'keep-alive',
          'X-Accel-Buffering': 'no',
        });
        wallpaperEngineBridgeEventClients.add(res);
        res.write(': mineradio-wallpaper-bridge\n\n');
        writeBridgeSseState(res);
        req.on('close', () => {
          wallpaperEngineBridgeEventClients.delete(res);
        });
        return;
      }
      req.setTimeout(1200, () => {
        try { req.destroy(); } catch (_) {}
      });
      res.setTimeout(1200, () => {
        try { res.destroy(); } catch (_) {}
      });
      if (req.method === 'OPTIONS') {
        res.writeHead(204, {
          'Access-Control-Allow-Origin': '*',
          'Access-Control-Allow-Methods': 'GET, OPTIONS',
          'Access-Control-Allow-Headers': 'Content-Type',
        });
        res.end();
        return;
      }
      if (url.pathname === '/state') {
        writeBridgeJson(res, 200, wallpaperEngineBridgeState());
        return;
      }
      if (url.pathname === '/stage') {
        writeWallpaperEngineStage(res);
        return;
      }
      if (url.pathname === '/asset') {
        const src = url.searchParams.get('src') || '';
        const filePath = filePathFromBridgeAsset(src);
        if (!filePath || !fs.existsSync(filePath)) {
          writeBridgeJson(res, 404, { ok: false, error: 'ASSET_NOT_FOUND' });
          return;
        }
        writeBridgeFile(req, res, filePath);
        return;
      }
      writeBridgeJson(res, 404, { ok: false, error: 'NOT_FOUND' });
    } catch (e) {
      writeBridgeJson(res, 500, { ok: false, error: e.message || String(e) });
    }
  });
  wallpaperEngineBridgeServer.on('error', (error) => {
    wallpaperDebugLog('wallpaper-engine-bridge-error', { error: error.message || String(error) });
  });
  wallpaperEngineBridgeServer.keepAliveTimeout = 1000;
  wallpaperEngineBridgeServer.headersTimeout = 2500;
  wallpaperEngineBridgeServer.timeout = 0;
  wallpaperEngineBridgeServer.listen(wallpaperEngineBridgePort, '127.0.0.1', () => {
    wallpaperDebugLog('wallpaper-engine-bridge-start', { port: wallpaperEngineBridgePort });
  });
}

function stopWallpaperEngineBridgeServer() {
  if (!wallpaperEngineBridgeServer) return;
  for (const res of wallpaperEngineBridgeEventClients) {
    try { res.destroy(); } catch (_) {}
  }
  wallpaperEngineBridgeEventClients.clear();
  try { wallpaperEngineBridgeServer.close(); } catch (_) {}
  wallpaperEngineBridgeServer = null;
}

function nativeWallpaperExePath() {
  return path.join(__dirname, 'native-wallpaper', 'publish', 'Mineradio.NativeWallpaper.exe');
}

function nativeD3D11WallpaperExePath() {
  return path.join(__dirname, 'native-d3d11-wallpaper', 'publish', 'Mineradio.NativeD3D11Wallpaper.exe');
}

function nativeD3D11WallpaperPipeName() {
  return 'MineradioD3D11Wallpaper';
}

function nativeD3D11WallpaperPipePath() {
  return `\\\\.\\pipe\\${nativeD3D11WallpaperPipeName()}`;
}

function normalizeRemovedVisualPreset(preset, fallback = 5) {
  const value = Number(preset);
  if (!Number.isFinite(value)) return fallback;
  const rounded = Math.max(0, Math.min(7, Math.round(value)));
  return rounded === 6 ? 5 : rounded;
}

function cachedWallpaperTextureUrlFromName(name) {
  const base = path.basename(String(name || ''));
  if (!/^wallpaper-engine-capture-cache-[a-f0-9]+\.png$/i.test(base)) return '';
  const filePath = path.join(app.getPath('userData'), base);
  return fs.existsSync(filePath) ? pathToFileURL(filePath).href : '';
}

function sanitizeWallpaperStateString(value, maxLength = 2048) {
  if (value == null) return '';
  return String(value)
    .replace(/[\u0000-\u001f\u007f]/g, '')
    .replace(/"/g, '')
    .slice(0, maxLength);
}

function sanitizeWallpaperLyricText(value, maxLength = 2048) {
  if (value == null) return '';
  return String(value)
    .replace(/\r\n?/g, '\n')
    .replace(/[\u0000-\u0009\u000b-\u001f\u007f]/g, '')
    .replace(/"/g, '')
    .split('\n')
    .slice(0, 2)
    .map((row) => row.replace(/\s+/g, ' ').trim())
    .filter(Boolean)
    .join('\n')
    .slice(0, maxLength);
}

function sanitizeWallpaperStateStrings(next) {
  if (!next || typeof next !== 'object') return next;
  [
    'title',
    'artist',
    'cover',
    'texture',
    'textureType',
    'textureName',
    'wallpaperTexture',
    'wallpaperTextureType',
    'wallpaperTextureName',
    'particleMode',
    'quality',
    'frameRate',
    'desktopMode',
    'framePath',
    'frameSource',
    'frameFormat',
  ].forEach((key) => {
    if (Object.prototype.hasOwnProperty.call(next, key)) {
      next[key] = sanitizeWallpaperStateString(next[key], key.includes('Texture') || key === 'texture' || key === 'cover' || key === 'framePath' ? 4096 : 512);
    }
  });
  if (next.lyric && typeof next.lyric === 'object') {
    next.lyric = { ...next.lyric };
    if (Object.prototype.hasOwnProperty.call(next.lyric, 'text')) {
      next.lyric.text = sanitizeWallpaperLyricText(next.lyric.text, 2048);
    }
  }
  if (next.background && typeof next.background === 'object') {
    next.background = { ...next.background };
    if (Object.prototype.hasOwnProperty.call(next.background, 'source')) {
      next.background.source = sanitizeWallpaperStateString(next.background.source, 64);
    }
  }
  return next;
}

function normalizeWallpaperPayload(payload = {}) {
  const next = { ...(payload || {}) };
  sanitizeWallpaperStateStrings(next);
  next.frameRate = normalizeWallpaperFrameRate(next.frameRate);
  next.throttleFps = normalizeWallpaperThrottleFps(next.throttleFps);
  next.desktopMode = normalizeNativeDesktopMode(next.desktopMode || process.env.MINERADIO_DESKTOP_MODE || DEFAULT_NATIVE_DESKTOP_MODE);
  if (next.visual && typeof next.visual === 'object' && next.visual.preset != null) {
    const visualPreset = Number(next.visual.preset);
    if (Number.isFinite(visualPreset)) {
      next.visual = { ...next.visual, preset: normalizeRemovedVisualPreset(visualPreset, 5) };
      next.preset = next.visual.preset;
    }
  } else if (next.preset != null) {
    next.preset = normalizeRemovedVisualPreset(next.preset, 5);
  }
  const cachedTexture = cachedWallpaperTextureUrlFromName(next.textureName);
  const texture = String(next.texture || '');
  if (cachedTexture && (!texture || /\.gif(?:$|[?#])/i.test(texture) || /\/preview\.gif(?:$|[?#])/i.test(texture))) {
    next.texture = cachedTexture;
    next.textureType = 'image';
  }
  next.background = normalizeWallpaperBackgroundMetrics(next.background);
  return next;
}

function normalizeWallpaperFrameRate(value) {
  const normalized = String(value == null ? '' : value).toLowerCase();
  return /^(auto|30|60|90|120)$/.test(normalized) ? normalized : 'auto';
}

function normalizeWallpaperThrottleFps(value) {
  const fps = Number(value);
  return Number.isFinite(fps) ? Math.max(12, Math.min(120, Math.round(fps))) : 60;
}

function nativeD3D11PresetUsesCoverTexture(state = {}) {
  const visualPreset = state && state.visual && state.visual.preset != null ? Number(state.visual.preset) : NaN;
  const preset = Number.isFinite(visualPreset) ? visualPreset : Number(state && state.preset);
  if (!Number.isFinite(preset)) return false;
  const rounded = normalizeRemovedVisualPreset(preset, 5);
  return rounded === 0 || rounded === 1 || rounded === 2 || rounded === 4 || rounded === 5;
}

function wallpaperPresetForRenderer(state = {}) {
  const visualPreset = state && state.visual && state.visual.preset != null ? Number(state.visual.preset) : NaN;
  const preset = Number.isFinite(visualPreset) ? visualPreset : Number(state && state.preset);
  if (!Number.isFinite(preset)) return 5;
  return normalizeRemovedVisualPreset(preset, 5);
}

function shouldUseWebglSonicWallpaper(state = wallpaperState) {
  return USE_WEBGL_SONIC_WALLPAPER && wallpaperPresetForRenderer(state || {}) === 4;
}

function shouldUseNativeD3D11Wallpaper(state = wallpaperState) {
  return USE_NATIVE_D3D11_WALLPAPER && !shouldUseWebglSonicWallpaper(state || {});
}

function nativeD3D11RuntimeState(state = wallpaperState) {
  const next = normalizeWallpaperPayload(state || {});
  if (shouldUseNativeD3D11Wallpaper(next) && nativeD3D11PresetUsesCoverTexture(next) && next.cover) {
    next.wallpaperTexture = next.wallpaperTexture || next.texture || '';
    next.wallpaperTextureType = next.wallpaperTextureType || next.textureType || '';
    next.wallpaperTextureName = next.wallpaperTextureName || next.textureName || '';
    next.texture = next.cover;
    next.textureType = 'image';
    next.textureName = next.title || next.textureName || 'song-cover';
  }
  return next;
}

function normalizeWallpaperBackgroundMetrics(metrics = {}) {
  const source = metrics && typeof metrics === 'object' ? metrics : {};
  const clamp01 = (value, fallback) => {
    const number = Number(value);
    return Number.isFinite(number) ? Math.max(0, Math.min(1, number)) : fallback;
  };
  return {
    luma: clamp01(source.luma, 0),
    contrast: clamp01(source.contrast, 0),
    busy: clamp01(source.busy, 0),
    confidence: clamp01(source.confidence, 0),
    source: String(source.source || '').slice(0, 64),
  };
}

function wallpaperStateNeedsBackgroundMetrics(state = wallpaperState) {
  return !(state && state.background && Number(state.background.confidence) > 0);
}

function localPathFromWallpaperTextureUrl(value) {
  const raw = String(value || '').trim();
  if (!raw) return '';
  if (/^file:/i.test(raw)) {
    try {
      return fileURLToPath(raw);
    } catch (_) {
      return '';
    }
  }
  if (/^[a-zA-Z]:[\\/]/.test(raw) || raw.startsWith('\\\\')) return raw;
  return '';
}

function fillWallpaperBackgroundMetricsFromCurrentTexture(state = wallpaperState, reason = 'current-texture') {
  if (!state || !wallpaperStateNeedsBackgroundMetrics(state)) return state;
  const candidates = [
    state.wallpaperTexture,
    state.texture,
  ].map(localPathFromWallpaperTextureUrl).filter(Boolean);
  for (const filePath of candidates) {
    const background = analyzeWallpaperBackgroundImage(filePath, reason);
    if (!background || Number(background.confidence) <= 0) continue;
    const next = {
      ...state,
      background,
    };
    wallpaperDebugLog('wallpaper-background-filled-from-current-texture', {
      reason,
      filePath,
      background,
    });
    return next;
  }
  return state;
}

function mergeWallpaperPayloadPreservingBackground(base = {}, payload = {}) {
  const normalizedPayload = normalizeWallpaperPayload(payload || {});
  if (wallpaperStateNeedsBackgroundMetrics(normalizedPayload)
    && !wallpaperStateNeedsBackgroundMetrics(base)) {
    normalizedPayload.background = base.background;
  }
  return {
    ...base,
    ...normalizedPayload,
  };
}

const nativeWallpaperWriteSleepBuffer = new SharedArrayBuffer(4);
const nativeWallpaperWriteSleepArray = new Int32Array(nativeWallpaperWriteSleepBuffer);

function nativeWallpaperSleepSync(ms) {
  Atomics.wait(nativeWallpaperWriteSleepArray, 0, 0, ms);
}

function persistableWallpaperState(state = wallpaperState, options = {}) {
  const next = { ...(state || {}) };
  if (!options.realtime) {
    delete next.motion;
    delete next.lyric;
    delete next.playing;
  }
  delete next.title;
  delete next.artist;
  delete next.cover;
  return next;
}

function writeNativeWallpaperState() {
  const target = nativeWallpaperStatePath();
  let stateForDisk = persistableWallpaperState(wallpaperState, {
    realtime: !!(shouldUseNativeD3D11Wallpaper(wallpaperState) && wallpaperState && wallpaperState.enabled),
  });
  if (shouldUseNativeD3D11Wallpaper(wallpaperState)) {
    stateForDisk = nativeD3D11RuntimeState(stateForDisk);
  }
  if (USE_NATIVE_WALLPAPER_FRAME_PRESENTER && wallpaperState && wallpaperState.enabled) {
    wallpaperState = {
      ...wallpaperState,
      framePath: wallpaperStageFramePath(),
      frameSource: 'stage-capture',
      frameFormat: 'raw-bgra',
    };
    stateForDisk = wallpaperState;
    if (shouldUseNativeD3D11Wallpaper(wallpaperState)) {
      stateForDisk = nativeD3D11RuntimeState(stateForDisk);
    }
  }
  const body = JSON.stringify(stateForDisk || {});
  const tmp = `${target}.${process.pid}.${Date.now()}.tmp`;
  let lastError = null;
  try {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    for (let attempt = 0; attempt < 6; attempt += 1) {
      try {
        fs.writeFileSync(tmp, body, 'utf8');
        fs.renameSync(tmp, target);
        return;
      } catch (e) {
        lastError = e;
        try { fs.unlinkSync(tmp); } catch (_) {}
        nativeWallpaperSleepSync(18 + attempt * 24);
      }
    }
    fs.writeFileSync(target, body, 'utf8');
  } catch (e) {
    wallpaperDebugLog('native-state-write-failed', { error: e.message, lastError: lastError ? lastError.message : '' });
  } finally {
    try { fs.unlinkSync(tmp); } catch (_) {}
  }
}

function closeNativeWallpaperPipe() {
  if (nativeWallpaperPipeRetryTimer) {
    clearTimeout(nativeWallpaperPipeRetryTimer);
    nativeWallpaperPipeRetryTimer = null;
  }
  nativeWallpaperPipeConnected = false;
  nativeWallpaperPipeConnecting = false;
  nativeWallpaperPipeBackpressure = false;
  if (nativeWallpaperPipeClient) {
    try { nativeWallpaperPipeClient.destroy(); } catch (_) {}
  }
  nativeWallpaperPipeClient = null;
}

function scheduleNativeWallpaperPipeConnect(delay = 120) {
  if (!shouldUseNativeD3D11Wallpaper(wallpaperState) || process.platform !== 'win32') return;
  if (!nativeWallpaperProcess || nativeWallpaperProcess.killed) return;
  if (nativeWallpaperPipeConnected || nativeWallpaperPipeConnecting || nativeWallpaperPipeRetryTimer) return;
  nativeWallpaperPipeRetryTimer = setTimeout(connectNativeWallpaperPipe, delay);
}

function connectNativeWallpaperPipe() {
  nativeWallpaperPipeRetryTimer = null;
  if (!shouldUseNativeD3D11Wallpaper(wallpaperState) || process.platform !== 'win32') return;
  if (!nativeWallpaperProcess || nativeWallpaperProcess.killed) return;
  if (nativeWallpaperPipeConnected || nativeWallpaperPipeConnecting) return;
  try {
    nativeWallpaperPipeConnecting = true;
    const client = net.createConnection(nativeD3D11WallpaperPipePath());
    nativeWallpaperPipeClient = client;
    client.setNoDelay(true);
    client.on('connect', () => {
      nativeWallpaperPipeConnecting = false;
      nativeWallpaperPipeConnected = true;
      nativeWallpaperPipeBackpressure = false;
      wallpaperDebugLog('native-pipe-connected', { pipe: nativeD3D11WallpaperPipeName() });
      sendNativeWallpaperPipeState();
    });
    client.on('drain', () => {
      nativeWallpaperPipeBackpressure = false;
    });
    client.on('error', (error) => {
      nativeWallpaperPipeConnecting = false;
      nativeWallpaperPipeConnected = false;
      nativeWallpaperPipeBackpressure = false;
      const now = Date.now();
      if (now - nativeWallpaperPipeLastErrorAt > 3000) {
        nativeWallpaperPipeLastErrorAt = now;
        wallpaperDebugLog('native-pipe-error', { error: error.message || String(error) });
      }
    });
    client.on('close', () => {
      nativeWallpaperPipeConnecting = false;
      nativeWallpaperPipeConnected = false;
      nativeWallpaperPipeBackpressure = false;
      if (nativeWallpaperPipeClient === client) nativeWallpaperPipeClient = null;
      scheduleNativeWallpaperPipeConnect(450);
    });
  } catch (e) {
    nativeWallpaperPipeConnecting = false;
    wallpaperDebugLog('native-pipe-connect-failed', { error: e.message || String(e) });
    scheduleNativeWallpaperPipeConnect(650);
  }
}

function sendNativeWallpaperPipeState() {
  if (!nativeWallpaperPipeClient || !nativeWallpaperPipeConnected) return false;
  if (nativeWallpaperPipeBackpressure) return false;
  try {
    const runtimeState = nativeD3D11RuntimeState(wallpaperState || {});
    debugNativeWallpaperMotionSample(runtimeState);
    const accepted = nativeWallpaperPipeClient.write(`${JSON.stringify(runtimeState)}\n`);
    nativeWallpaperPipeBackpressure = !accepted;
    return true;
  } catch (e) {
    nativeWallpaperPipeConnected = false;
    nativeWallpaperPipeBackpressure = false;
    wallpaperDebugLog('native-pipe-write-failed', { error: e.message || String(e) });
    scheduleNativeWallpaperPipeConnect(350);
    return false;
  }
}

function debugNativeWallpaperMotionSample(runtimeState) {
  if (!isWallpaperMotionDebugEnabled()) return;
  const now = Date.now();
  if (now - nativeWallpaperMotionDebugLastAt < 1000) return;
  nativeWallpaperMotionDebugLastAt = now;
  const motion = runtimeState && runtimeState.motion ? runtimeState.motion : {};
  const num = (value) => {
    const n = Number(value);
    return Number.isFinite(n) ? Number(n.toFixed(4)) : 0;
  };
  wallpaperDebugLog('native-motion-sample', {
    enabled: !!(runtimeState && runtimeState.enabled),
    preset: runtimeState && runtimeState.preset,
    textureType: runtimeState && runtimeState.textureType,
    particleMode: runtimeState && runtimeState.particleMode,
    macro: {
      structure: num(motion.structure),
      motionFlow: num(motion.motionFlow),
      impact: num(motion.impact),
      impactPulse: num(motion.impactPulse),
      impactStrength: num(motion.impactStrength),
      gloss: num(motion.gloss),
    },
    source: {
      bass: num(motion.bass),
      mid: num(motion.mid),
      treble: num(motion.treble),
      beat: num(motion.beat),
      energy: num(motion.energy),
      subBass: num(motion.subBass),
      bassBody: num(motion.bassBody),
      midFlow: num(motion.midFlow),
      kickEnvelope: num(motion.kickEnvelope),
      kickConfidence: num(motion.kickConfidence),
    },
  });
}

function isWallpaperMotionDebugEnabled() {
  if (process.env.MINERADIO_DEBUG_WALLPAPER_MOTION === '1') return true;
  try {
    return fs.existsSync(wallpaperMotionDebugFlagPath());
  } catch (_) {
    return false;
  }
}

function readNativeWallpaperState() {
  try {
    const target = nativeWallpaperStatePath();
    if (!fs.existsSync(target)) return null;
    let text = fs.readFileSync(target, 'utf8').replace(/^\uFEFF/, '').trimStart();
    const jsonStart = text.indexOf('{');
    if (jsonStart > 0) text = text.slice(jsonStart);
    const parsed = JSON.parse(text);
    if (!parsed || typeof parsed !== 'object') return null;
    return normalizeWallpaperPayload(parsed);
  } catch (e) {
    wallpaperDebugLog('native-state-read-failed', { error: e.message || String(e) });
    return null;
  }
}

function restoreWallpaperWindowFromSavedState() {
  const savedState = readNativeWallpaperState();
  if (!savedState || savedState.enabled !== true) return;
  try {
    wallpaperState = { ...wallpaperState, ...savedState, enabled: true };
    wallpaperDebugLog('restore-saved-wallpaper-state', {
      hasTexture: !!wallpaperState.texture,
      textureName: wallpaperState.textureName || '',
    });
    const win = createWallpaperWindow(wallpaperState);
    broadcastWallpaperEnabledState(true);
    wallpaperDebugLog('restore-saved-wallpaper-state-ok', {
      bounds: win && !win.isDestroyed() ? win.getBounds() : null,
    });
  } catch (e) {
    wallpaperDebugLog('restore-saved-wallpaper-state-failed', { error: e.message || String(e) });
  }
}

function clearSavedWallpaperEnabledState(reason = 'startup-disabled') {
  const savedState = readNativeWallpaperState();
  if (!savedState || savedState.enabled !== true) return;
  wallpaperState = { ...wallpaperState, ...savedState, enabled: false };
  writeNativeWallpaperState();
  wallpaperDebugLog('clear-saved-wallpaper-state', {
    reason,
    hadTexture: !!savedState.texture,
    textureName: savedState.textureName || '',
  });
}

function createNativeWallpaperWindow(payload = {}) {
  wallpaperState = { ...mergeWallpaperPayloadPreservingBackground(wallpaperState, payload), enabled: true };
  wallpaperState = fillWallpaperBackgroundMetricsFromCurrentTexture(wallpaperState, 'native-start-current-texture');
  writeNativeWallpaperState();
  if (USE_NATIVE_D3D11_WALLPAPER && !shouldUseNativeD3D11Wallpaper(wallpaperState)) {
    wallpaperDebugLog('native-start-skip-webgl-preset', { preset: wallpaperPresetForRenderer(wallpaperState) });
    return null;
  }
  const exe = USE_NATIVE_D3D11_WALLPAPER ? nativeD3D11WallpaperExePath() : nativeWallpaperExePath();
  if (!fs.existsSync(exe)) {
    wallpaperDebugLog('native-start-missing-exe', { exe, renderer: USE_NATIVE_D3D11_WALLPAPER ? 'd3d11' : 'legacy' });
    return null;
  }
  if (nativeWallpaperProcess && !nativeWallpaperProcess.killed) {
    if (USE_NATIVE_D3D11_WALLPAPER) {
      scheduleNativeWallpaperPipeConnect(0);
      sendWallpaperState({ persist: false });
    }
    return nativeWallpaperWindowShim;
  }
  const args = [nativeWallpaperStatePath()];
  if (USE_NATIVE_D3D11_WALLPAPER) {
    const desktopMode = normalizeNativeDesktopMode(wallpaperState.desktopMode || process.env.MINERADIO_DESKTOP_MODE || DEFAULT_NATIVE_DESKTOP_MODE);
    args.push(`--pipe=${nativeD3D11WallpaperPipeName()}`);
    args.push(nativeDesktopModeLaunchArg(desktopMode));
    wallpaperDebugLog('native-launch-args', {
      message: `[Mineradio] Launch native args: ${args.join(' ')}`,
      desktopModeMessage: `[Mineradio] Desktop mode: ${desktopMode}`,
      desktopMode,
      args,
    });
  }
  if (USE_NATIVE_WALLPAPER_FRAME_PRESENTER && !USE_NATIVE_WALLPAPER) args.push('--no-parent');
  closeNativeWallpaperPipe();
  nativeWallpaperProcess = spawn(exe, args, {
    windowsHide: true,
    stdio: 'ignore',
  });
  nativeWallpaperProcess.once('exit', (code, signal) => {
    wallpaperDebugLog('native-exit', { code, signal });
    nativeWallpaperProcess = null;
    closeNativeWallpaperPipe();
    if (wallpaperWindow === nativeWallpaperWindowShim) wallpaperWindow = null;
    nativeWallpaperWindowShim = null;
    if (shouldUseNativeD3D11Wallpaper(wallpaperState) && wallpaperState && wallpaperState.enabled && !isQuitting) {
      scheduleNativeWallpaperRestart('native-exit');
      return;
    }
    broadcastWallpaperEnabledState(false);
  });
  nativeWallpaperWindowShim = {
    nativeWallpaper: true,
    isDestroyed: () => !nativeWallpaperProcess || nativeWallpaperProcess.killed,
    isVisible: () => !!nativeWallpaperProcess && !nativeWallpaperProcess.killed,
    showInactive: () => {},
    setBounds: () => {},
    getBounds: () => wallpaperElectronBounds(),
    close: () => {
      if (nativeWallpaperProcess && !nativeWallpaperProcess.killed) nativeWallpaperProcess.kill();
      nativeWallpaperProcess = null;
    },
    once: () => {},
    removeListener: () => {},
    webContents: { send: () => {}, once: () => {}, removeListener: () => {} },
  };
  wallpaperWindowLoadReady = true;
  scheduleNativeWallpaperPipeConnect(180);
  wallpaperDebugLog('native-start', {
    exe,
    args,
    statePath: nativeWallpaperStatePath(),
    pid: nativeWallpaperProcess.pid,
    renderer: USE_NATIVE_D3D11_WALLPAPER ? 'd3d11' : 'legacy',
  });
  return nativeWallpaperWindowShim;
}

function scheduleNativeWallpaperRestart(reason = 'restart') {
  if (!shouldUseNativeD3D11Wallpaper(wallpaperState) || isQuitting) return;
  if (!wallpaperState || !wallpaperState.enabled) return;
  if (nativeWallpaperRestartTimer) clearTimeout(nativeWallpaperRestartTimer);
  nativeWallpaperRestartTimer = setTimeout(() => {
    nativeWallpaperRestartTimer = null;
    if (isQuitting || !wallpaperState || !wallpaperState.enabled) return;
    wallpaperDebugLog('native-restart', { reason });
    const win = createNativeWallpaperWindow(wallpaperState);
    wallpaperWindow = win;
    scheduleNativeWallpaperPipeConnect(250);
    sendWallpaperStateSoon(500, { persist: false });
  }, 900);
}

function ensureNativeWallpaperFramePresenter(payload = {}) {
  if (!USE_NATIVE_WALLPAPER_FRAME_PRESENTER || process.platform !== 'win32') return null;
  wallpaperState = {
    ...wallpaperState,
    ...normalizeWallpaperPayload(payload),
    enabled: true,
    framePath: wallpaperStageFramePath(),
    frameSource: 'stage-capture',
    frameFormat: 'raw-bgra',
  };
  return createNativeWallpaperWindow(wallpaperState);
}

function stopWallpaperClipController() {
  if (wallpaperClipProcess && !wallpaperClipProcess.killed) {
    try { wallpaperClipProcess.kill(); } catch (_) {}
  }
  wallpaperClipProcess = null;
  wallpaperClipProcessHwnd = '';
}

function startWallpaperClipController(win) {
  if (!WALLPAPER_CLIP_CONTROLLER_ENABLED) return;
  if (!USE_DIRECT_ELECTRON_WALLPAPER || process.platform !== 'win32' || !win || win.isDestroyed()) return;
  const hwnd = nativeWindowHandleDecimal(win);
  if (!hwnd) return;
  if (wallpaperClipProcess && !wallpaperClipProcess.killed && wallpaperClipProcessHwnd === hwnd) return;
  stopWallpaperClipController();
  const exe = nativeWallpaperExePath();
  if (!fs.existsSync(exe)) {
    wallpaperDebugLog('clip-start-skip', { reason: 'missing-exe', exe });
    return;
  }
  wallpaperClipProcessHwnd = hwnd;
  wallpaperClipProcess = spawn(exe, ['--clip-window', hwnd], {
    windowsHide: true,
    stdio: 'ignore',
  });
  wallpaperClipProcess.once('exit', (code, signal) => {
    wallpaperDebugLog('clip-exit', { hwnd, code, signal });
    if (wallpaperClipProcessHwnd === hwnd) {
      wallpaperClipProcess = null;
      wallpaperClipProcessHwnd = '';
    }
  });
  wallpaperDebugLog('clip-start', { exe, hwnd, pid: wallpaperClipProcess.pid });
}

const CHROMIUM_PERFORMANCE_SWITCHES = [
  ['autoplay-policy', 'no-user-gesture-required'],
];
for (const [name, value] of CHROMIUM_PERFORMANCE_SWITCHES) {
  if (value == null) app.commandLine.appendSwitch(name);
  else app.commandLine.appendSwitch(name, value);
}
const gotSingleInstanceLock = app.requestSingleInstanceLock();

const QQ_LOGIN_COOKIE_PRIORITY = [
  'uin',
  'qqmusic_uin',
  'wxuin',
  'login_type',
  'qm_keyst',
  'qqmusic_key',
  'p_skey',
  'skey',
  'psrf_qqopenid',
  'psrf_qqunionid',
  'psrf_qqaccess_token',
  'psrf_qqrefresh_token',
  'wxopenid',
  'wxunionid',
  'wxrefresh_token',
  'wxskey',
  'p_uin',
  'ptcz',
  'RK',
];
const NETEASE_LOGIN_COOKIE_PRIORITY = [
  'MUSIC_U',
  '__csrf',
  'NMTID',
  'MUSIC_A',
  '__remember_me',
  '_ntes_nuid',
  '_ntes_nnid',
  'WEVNSM',
  'WNMCID',
  'JSESSIONID-WYYY',
];

function findOpenPort(startPort) {
  return new Promise((resolve, reject) => {
    function tryPort(port) {
      const tester = net.createServer();

      tester.once('error', (err) => {
        if (err.code === 'EADDRINUSE' || err.code === 'EACCES') {
          tryPort(port + 1);
          return;
        }
        reject(err);
      });

      tester.once('listening', () => {
        tester.close(() => resolve(port));
      });

      tester.listen(port, '127.0.0.1');
    }

    tryPort(startPort);
  });
}

function waitForServer(server) {
  if (!server || server.listening) return Promise.resolve();

  return new Promise((resolve, reject) => {
    server.once('listening', resolve);
    server.once('error', reject);
  });
}

function sendWindowState(win) {
  if (!win || win.isDestroyed()) return;
  win.webContents.send('desktop-window-state', getWindowState(win));
}

function sendWindowRestoreAnimation(win) {
  if (!win || win.isDestroyed()) return;
  win.webContents.send('desktop-window-restore-animation', { at: Date.now() });
}

function sendGlobalHotkeyAction(action) {
  if (!mainWindow || mainWindow.isDestroyed() || !action) return;
  mainWindow.webContents.send('mineradio-global-hotkey', { action });
}

function pausePlaybackBeforeQuit(reason) {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  try {
    mainWindow.webContents.send('mineradio-global-hotkey', { action: 'pause', source: reason || 'quit' });
  } catch (_) {}
}

function unregisterMineradioGlobalHotkeys() {
  for (const accelerator of registeredGlobalHotkeys.keys()) {
    try { globalShortcut.unregister(accelerator); } catch (e) {}
  }
  registeredGlobalHotkeys.clear();
}

function configureMineradioGlobalHotkeys(bindings = []) {
  unregisterMineradioGlobalHotkeys();
  const results = [];
  const seen = new Set();
  for (const item of Array.isArray(bindings) ? bindings : []) {
    const action = item && String(item.action || '').trim();
    const accelerator = item && String(item.accelerator || '').trim();
    if (!action || !accelerator || seen.has(accelerator)) continue;
    seen.add(accelerator);
    let registered = false;
    try {
      registered = globalShortcut.register(accelerator, () => sendGlobalHotkeyAction(action));
    } catch (error) {
      registered = false;
    }
    if (registered) {
      registeredGlobalHotkeys.set(accelerator, action);
      results.push({ action, accelerator, ok: true });
    } else {
      results.push({
        action,
        accelerator,
        ok: false,
        conflict: {
          sourceName: '系统 / 其他软件',
          sourceIcon: 'warning',
          reason: '该组合键已被占用或被系统保留',
        },
      });
    }
  }
  return { ok: true, results };
}

function scheduleWindowStateSend(win, delay = 80) {
  if (!win || win.isDestroyed()) return;
  if (mainWindowStateTimer) clearTimeout(mainWindowStateTimer);
  mainWindowStateTimer = setTimeout(() => {
    mainWindowStateTimer = null;
    sendWindowState(win);
  }, delay);
}

function rectsOverlapOnY(a, b) {
  if (!a || !b) return false;
  const aTop = Number(a.y) || 0;
  const bTop = Number(b.y) || 0;
  const aBottom = aTop + (Number(a.height) || 0);
  const bBottom = bTop + (Number(b.height) || 0);
  return aBottom > bTop && bBottom > aTop;
}

function getDisplayState(win) {
  const displays = screen.getAllDisplays();
  const primary = screen.getPrimaryDisplay();
  const display = win && !win.isDestroyed()
    ? screen.getDisplayMatching(win.getBounds())
    : primary;
  const bounds = display && display.bounds ? display.bounds : primary.bounds;
  const displayId = display && display.id;
  const primaryId = primary && primary.id;
  const edgeTolerance = 2;
  const hasDisplayOnLeft = displays.some((candidate) => {
    if (!candidate || candidate.id === displayId || !candidate.bounds) return false;
    return rectsOverlapOnY(bounds, candidate.bounds)
      && Math.abs((candidate.bounds.x + candidate.bounds.width) - bounds.x) <= edgeTolerance;
  });
  const hasDisplayOnRight = displays.some((candidate) => {
    if (!candidate || candidate.id === displayId || !candidate.bounds) return false;
    return rectsOverlapOnY(bounds, candidate.bounds)
      && Math.abs((bounds.x + bounds.width) - candidate.bounds.x) <= edgeTolerance;
  });
  return {
    displayId,
    primaryDisplayId: primaryId,
    isPrimaryDisplay: !!(display && primary && display.id === primary.id),
    hasDisplayOnLeft,
    hasDisplayOnRight,
    displayBounds: bounds ? {
      x: bounds.x,
      y: bounds.y,
      width: bounds.width,
      height: bounds.height,
    } : null,
  };
}

function getWindowState(win) {
  if (!win || win.isDestroyed()) return {
    isMaximized: false,
    windowMaximized: false,
    isNativeFullScreen: false,
    isHtmlFullScreen: false,
    isWindowFullScreen: false,
    windowFullscreen: false,
    isAnyFullscreen: false,
    isFullScreen: false,
    isMinimized: false,
    isVisible: false,
    isFocused: false,
    isPrimaryDisplay: true,
    hasDisplayOnLeft: false,
    hasDisplayOnRight: false,
    displayBounds: null,
  };
  const windowFullscreen = win.isFullScreen() || windowFullscreenActive;
  const htmlFullscreen = htmlFullscreenActive;
  return {
    isMaximized: win.isMaximized(),
    windowMaximized: win.isMaximized(),
    isNativeFullScreen: win.isFullScreen(),
    isHtmlFullScreen: htmlFullscreen,
    isWindowFullScreen: windowFullscreenActive,
    windowFullscreen,
    isAnyFullscreen: windowFullscreen || htmlFullscreen,
    isFullScreen: windowFullscreen,
    isMinimized: win.isMinimized(),
    isVisible: win.isVisible(),
    isFocused: win.isFocused(),
    ...getDisplayState(win),
  };
}

function getSenderWindow(event) {
  return BrowserWindow.fromWebContents(event.sender);
}

function focusMainWindow() {
  if (!mainWindow || mainWindow.isDestroyed()) return false;
  const wasMinimized = mainWindow.isMinimized();
  const wasHidden = !mainWindow.isVisible();
  if (wasMinimized) mainWindow.restore();
  if (!mainWindow.isVisible()) mainWindow.show();
  mainWindow.focus();
  if (wasMinimized || wasHidden) sendWindowRestoreAnimation(mainWindow);
  sendWindowState(mainWindow);
  updateTrayMenu();
  return true;
}

function wallpaperCommandFromArgs(args = []) {
  const list = Array.isArray(args) ? args.map((arg) => String(arg || '').toLowerCase()) : [];
  if (list.includes('--mineradio-wallpaper-enable')) return 'enable';
  if (list.includes('--mineradio-wallpaper-disable')) return 'disable';
  return '';
}

function playerCommandFromArgs(args = []) {
  const list = Array.isArray(args) ? args.map((arg) => String(arg || '').toLowerCase()) : [];
  if (list.includes('--mineradio-toggle-play')) return 'toggle-play';
  return '';
}

function wallpaperPresetFromArgs(args = []) {
  if (!Array.isArray(args)) return null;
  for (let i = 0; i < args.length; i += 1) {
    const arg = String(args[i] || '').toLowerCase();
    const inline = arg.match(/^--mineradio-wallpaper-preset=(\d+)$/);
    if (inline) return Number(inline[1]);
    if (arg === '--mineradio-wallpaper-preset' && i + 1 < args.length) {
      const next = Number(String(args[i + 1] || '').trim());
      if (Number.isFinite(next)) return next;
    }
  }
  return null;
}

async function applyWallpaperCommand(command, source = 'argv') {
  if (!command) return false;
  if (!mainWindow || mainWindow.isDestroyed()) return false;
  try {
    const script = command === 'enable'
      ? `
        (async function(){
          var api = typeof getDesktopWindowApi === 'function' ? getDesktopWindowApi() : null;
          var actualEnabled = !!(typeof fx !== 'undefined' && fx && fx.wallpaperMode);
          if (api && typeof api.getWallpaperModeState === 'function') {
            try {
              var actualState = await api.getWallpaperModeState();
              actualEnabled = !!(actualState && actualState.enabled);
            } catch (_) {}
          }
          if (!actualEnabled && typeof enableHomeWallpaperFromHome === 'function') {
            await enableHomeWallpaperFromHome();
          }
          return { ok: true, command: 'enable', alreadyEnabled: actualEnabled };
        })();
      `
      : `
        (async function(){
          var api = typeof getDesktopWindowApi === 'function' ? getDesktopWindowApi() : null;
          var actualEnabled = !!(typeof fx !== 'undefined' && fx && fx.wallpaperMode);
          if (api && typeof api.getWallpaperModeState === 'function') {
            try {
              var actualState = await api.getWallpaperModeState();
              actualEnabled = !!(actualState && actualState.enabled);
            } catch (_) {}
          }
          if (actualEnabled && typeof enableHomeWallpaperFromHome === 'function') {
            await enableHomeWallpaperFromHome();
          }
          return { ok: true, command: 'disable', alreadyEnabled: actualEnabled };
        })();
      `;
    const result = await mainWindow.webContents.executeJavaScript(script, true);
    wallpaperDebugLog('wallpaper-command-applied', { command, source, result });
    return true;
  } catch (e) {
    wallpaperDebugLog('wallpaper-command-failed', { command, source, error: e.message || String(e) });
    return false;
  }
}

async function applyWallpaperPresetCommand(preset, source = 'argv') {
  if (!Number.isFinite(preset)) return false;
  if (!mainWindow || mainWindow.isDestroyed()) return false;
  const safePreset = Math.max(0, Math.min(7, Math.round(preset)));
  try {
    const script = `
      (async function(){
        var preset = ${safePreset};
        if (typeof syncWallpaperPresetFromPlayer !== 'function') return { ok: false, error: 'PRESET_SYNC_MISSING', preset: preset };
        var ok = syncWallpaperPresetFromPlayer(preset, { push: true, save: true, forcePush: true });
        return { ok: !!ok, preset: preset, wallpaperMode: typeof fx !== 'undefined' && fx ? !!fx.wallpaperMode : null };
      })();
    `;
    const result = await mainWindow.webContents.executeJavaScript(script, true);
    wallpaperDebugLog('wallpaper-preset-command-applied', { preset: safePreset, source, result });
    return true;
  } catch (e) {
    wallpaperDebugLog('wallpaper-preset-command-failed', { preset: safePreset, source, error: e.message || String(e) });
    return false;
  }
}

async function applyPlayerCommand(command, source = 'argv') {
  if (!command) return false;
  if (!mainWindow || mainWindow.isDestroyed()) return false;
  try {
    const script = `
      (async function(){
        if (typeof togglePlay !== 'function') return { ok: false, error: 'TOGGLE_PLAY_MISSING' };
        await togglePlay();
        return { ok: true, command: 'toggle-play', playing: typeof playing !== 'undefined' ? !!playing : null };
      })();
    `;
    const result = await mainWindow.webContents.executeJavaScript(script, true);
    wallpaperDebugLog('player-command-applied', { command, source, result });
    return true;
  } catch (e) {
    wallpaperDebugLog('player-command-failed', { command, source, error: e.message || String(e) });
    return false;
  }
}

async function handleWallpaperCommandArgs(args = [], source = 'argv') {
  const command = wallpaperCommandFromArgs(args);
  if (!command) return false;
  if (!focusMainWindow() && app.isReady()) {
    try { await createWindow(); } catch (e) { console.error('Wallpaper command window restore failed:', e); }
  }
  return applyWallpaperCommand(command, source);
}

async function handlePlayerCommandArgs(args = [], source = 'argv') {
  const command = playerCommandFromArgs(args);
  if (!command) return false;
  if (!focusMainWindow() && app.isReady()) {
    try { await createWindow(); } catch (e) { console.error('Player command window restore failed:', e); }
  }
  return applyPlayerCommand(command, source);
}

async function handleWallpaperPresetArgs(args = [], source = 'argv') {
  const preset = wallpaperPresetFromArgs(args);
  if (!Number.isFinite(preset)) return false;
  if (!focusMainWindow() && app.isReady()) {
    try { await createWindow(); } catch (e) { console.error('Wallpaper preset command window restore failed:', e); }
  }
  return applyWallpaperPresetCommand(preset, source);
}

function hideMainWindowToBackground(win) {
  if (!win || win.isDestroyed()) return;
  if (win.isFullScreen()) exitFullscreenToWindow(win);
  if (win.isMinimized()) win.restore();
  win.hide();
  sendWindowState(win);
}

function showMainWindowFromTray() {
  if (focusMainWindow()) return;
  if (app.isReady()) createWindow().catch((e) => console.error('Tray restore failed:', e));
}

function quitAppFromTray() {
  pausePlaybackBeforeQuit('tray-quit');
  isQuitting = true;
  app.quit();
}

function sendTrayPlayerAction(action) {
  if (!action) return;
  const payload = typeof action === 'object' && action
    ? { ...action, source: 'tray' }
    : { action, source: 'tray' };
  if (!payload.action) return;
  if (!mainWindow || mainWindow.isDestroyed()) {
    showMainWindowFromTray();
    return;
  }
  mainWindow.webContents.send('mineradio-global-hotkey', payload);
}

function taskbarSvgIcon(pathData) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 24 24"><path fill="#ffffff" d="${pathData}"/></svg>`;
  return nativeImage.createFromDataURL(`data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`);
}

function getTaskbarThumbIcons() {
  if (taskbarThumbIcons) return taskbarThumbIcons;
  taskbarThumbIcons = {
    prev: taskbarSvgIcon('M6 5h2v14H6zM19 6.5v11L10 12z'),
    play: taskbarSvgIcon('M8 5.5v13l11-6.5z'),
    pause: taskbarSvgIcon('M7 5h4v14H7zM13 5h4v14h-4z'),
    next: taskbarSvgIcon('M16 5h2v14h-2zM5 17.5v-11l9 5.5z'),
  };
  return taskbarThumbIcons;
}

function taskbarWindowTitle() {
  const title = String(trayPlayerState.title || '').trim();
  const artist = String(trayPlayerState.artist || '').trim();
  if (!title) return APP_NAME;
  return artist ? `${title} - ${artist} · ${APP_NAME}` : `${title} · ${APP_NAME}`;
}

function normalizeTaskbarThumbnailClip(input) {
  if (!input || typeof input !== 'object') return null;
  const x = Math.round(Number(input.x));
  const y = Math.round(Number(input.y));
  const width = Math.round(Number(input.width));
  const height = Math.round(Number(input.height));
  if (![x, y, width, height].every(Number.isFinite)) return null;
  if (width < 16 || height < 16) return null;
  return {
    x: Math.max(0, x),
    y: Math.max(0, y),
    width: Math.max(16, width),
    height: Math.max(16, height),
  };
}

function updateMainWindowTaskbarState() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  try { mainWindow.setTitle(taskbarWindowTitle()); } catch (e) {}
  try {
    if (process.platform === 'win32' && typeof mainWindow.setThumbnailToolTip === 'function') {
      mainWindow.setThumbnailToolTip(taskbarWindowTitle());
    }
  } catch (e) {}
  try {
    const clip = normalizeTaskbarThumbnailClip(trayPlayerState.thumbnailClip);
    if (process.platform === 'win32' && clip && typeof mainWindow.setThumbnailClip === 'function') {
      mainWindow.setThumbnailClip(clip);
    }
  } catch (e) {}
  if (process.platform !== 'win32' || typeof mainWindow.setThumbarButtons !== 'function') return;
  try {
    const icons = getTaskbarThumbIcons();
    mainWindow.setThumbarButtons([
      { tooltip: '上一首', icon: icons.prev, click: () => sendTrayPlayerAction('prevTrack') },
      { tooltip: trayPlayerState.playing ? '暂停' : '播放', icon: trayPlayerState.playing ? icons.pause : icons.play, click: () => sendTrayPlayerAction('togglePlay') },
      { tooltip: '下一首', icon: icons.next, click: () => sendTrayPlayerAction('nextTrack') },
    ]);
  } catch (e) {}
}

function traySongLabel() {
  const title = String(trayPlayerState.title || '').trim();
  const artist = String(trayPlayerState.artist || '').trim();
  const text = title ? (artist ? `${title} - ${artist}` : title) : 'Mineradio';
  return text.length > 42 ? `${text.slice(0, 39)}...` : text;
}

function ensureTray() {
  if (tray && !tray.isDestroyed()) {
    updateTrayMenu();
    return tray;
  }
  const icon = fs.existsSync(APP_ICON_ICO) ? APP_ICON_ICO : process.execPath;
  tray = new Tray(icon);
  tray.setToolTip(APP_NAME);
  try { tray.setContextMenu(null); } catch (e) {}
  tray.on('click', showMainWindowFromTray);
  tray.on('double-click', showMainWindowFromTray);
  tray.on('right-click', showTrayMenuWindow);
  updateTrayMenu();
  return tray;
}

function trayMenuState() {
  const settings = readDesktopSettings();
  const state = mainWindow && !mainWindow.isDestroyed() ? getWindowState(mainWindow) : getWindowState(null);
  return {
    ...trayPlayerState,
    songLabel: traySongLabel(),
    closeToTray: !!settings.closeToTray,
    mainVisible: !!(mainWindow && !mainWindow.isDestroyed() && mainWindow.isVisible()),
    mainMinimized: !!state.isMinimized,
    windowMaximized: !!state.windowMaximized,
    windowFullscreen: !!state.windowFullscreen,
    htmlFullscreen: !!state.isHtmlFullScreen,
  };
}

function sendTrayMenuState() {
  if (!trayMenuWindow || trayMenuWindow.isDestroyed()) return;
  trayMenuWindow.webContents.send('mineradio-tray-menu-state', trayMenuState());
}

function updateTrayMenu() {
  if (!tray || tray.isDestroyed()) return;
  tray.setToolTip(traySongLabel());
  updateMainWindowTaskbarState();
  sendTrayMenuState();
}

function closeTrayMenuWindow() {
  if (!trayMenuWindow || trayMenuWindow.isDestroyed()) return;
  trayMenuWindow.close();
}

function hideTrayMenuWindow() {
  if (!trayMenuWindow || trayMenuWindow.isDestroyed()) return;
  trayMenuWindow.hide();
}

function positionTrayMenuWindow() {
  if (!trayMenuWindow || trayMenuWindow.isDestroyed()) return;
  const trayBounds = tray && !tray.isDestroyed() ? tray.getBounds() : null;
  const point = trayBounds && trayBounds.width && trayBounds.height
    ? {
        x: Math.round(trayBounds.x + trayBounds.width / 2),
        y: Math.round(trayBounds.y + trayBounds.height / 2),
      }
    : screen.getCursorScreenPoint();
  const display = screen.getDisplayNearestPoint(point);
  const area = display.workArea;
  const centerX = trayBounds && trayBounds.width ? trayBounds.x + trayBounds.width / 2 : point.x;
  const centerY = trayBounds && trayBounds.height ? trayBounds.y + trayBounds.height / 2 : point.y;
  const placeBelow = centerY < area.y + area.height / 2;
  const x = Math.round(Math.max(area.x + 8, Math.min(centerX - TRAY_MENU_WIDTH / 2, area.x + area.width - TRAY_MENU_WIDTH - 8)));
  const height = Math.max(TRAY_MENU_MIN_HEIGHT, Math.min(TRAY_MENU_MAX_HEIGHT, trayMenuHeight || TRAY_MENU_DEFAULT_HEIGHT));
  const y = Math.round(placeBelow
    ? Math.min(area.y + area.height - height - 8, centerY + 10)
    : Math.max(area.y + 8, centerY - height - 10));
  trayMenuWindow.setBounds({ x, y, width: TRAY_MENU_WIDTH, height });
}

function createTrayMenuWindow() {
  if (trayMenuWindow && !trayMenuWindow.isDestroyed()) return trayMenuWindow;
  trayMenuWindow = new BrowserWindow({
    width: TRAY_MENU_WIDTH,
    height: trayMenuHeight || TRAY_MENU_DEFAULT_HEIGHT,
    show: false,
    frame: false,
    transparent: true,
    resizable: false,
    movable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    focusable: true,
    backgroundColor: '#00000000',
    autoHideMenuBar: true,
    title: `${APP_NAME} Tray`,
    icon: APP_ICON_ICO,
    webPreferences: {
      preload: path.join(__dirname, 'tray-menu-preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  trayMenuWindow.setAlwaysOnTop(true, 'pop-up-menu');
  trayMenuWindow.on('blur', () => hideTrayMenuWindow());
  trayMenuWindow.on('closed', () => {
    trayMenuWindow = null;
  });
  trayMenuWindow.loadURL(overlayUrl('tray-menu.html')).catch((e) => console.warn('Tray menu load failed:', e.message));
  return trayMenuWindow;
}

function showTrayMenuWindow() {
  if (!mainServerPort) {
    createWindow().catch((e) => console.error('Tray menu server start failed:', e));
    return;
  }
  const win = createTrayMenuWindow();
  if (win.isVisible()) {
    win.hide();
    return;
  }
  positionTrayMenuWindow();
  win.show();
  win.focus();
  sendTrayMenuState();
}

function getUpdateDownloadDir() {
  return path.join(app.getPath('userData'), 'updates');
}

function shouldEnsureDesktopShortcut() {
  if (process.platform !== 'win32') return false;
  if (process.env.MINERADIO_NO_DESKTOP_SHORTCUT === '1') return false;
  return app.isPackaged || process.env.MINERADIO_CREATE_DESKTOP_SHORTCUT === '1';
}

function ensureDesktopShortcut() {
  if (!shouldEnsureDesktopShortcut()) return { ok: false, skipped: true };
  try {
    const shortcutPath = path.join(app.getPath('desktop'), `${APP_NAME}.lnk`);
    const target = process.execPath;
    const shortcut = {
      target,
      cwd: path.dirname(target),
      args: '',
      description: 'Mineradio desktop music player',
      icon: fs.existsSync(APP_ICON_ICO) ? APP_ICON_ICO : target,
      iconIndex: 0,
      appUserModelId: APP_USER_MODEL_ID,
    };

    if (fs.existsSync(shortcutPath) && shell.readShortcutLink) {
      try {
        const existing = shell.readShortcutLink(shortcutPath);
        if (existing && path.resolve(existing.target || '') === path.resolve(target) && String(existing.args || '') === '') {
          return { ok: true, path: shortcutPath, existing: true };
        }
      } catch (_) {}
      shell.writeShortcutLink(shortcutPath, 'replace', shortcut);
    } else {
      shell.writeShortcutLink(shortcutPath, 'create', shortcut);
    }
    return { ok: true, path: shortcutPath, created: true };
  } catch (e) {
    console.warn('Desktop shortcut creation skipped:', e.message);
    return { ok: false, error: e.message || 'DESKTOP_SHORTCUT_FAILED' };
  }
}

function parseCookieHeader(cookieText) {
  const out = {};
  String(cookieText || '').split(';').forEach((part) => {
    const raw = String(part || '').trim();
    if (!raw) return;
    const idx = raw.indexOf('=');
    if (idx <= 0) return;
    out[raw.slice(0, idx).trim()] = raw.slice(idx + 1).trim();
  });
  return out;
}

function qqCookieHasLogin(cookieText) {
  const obj = parseCookieHeader(cookieText);
  const rawUin = Number(obj.login_type) === 2
    ? (obj.wxuin || obj.uin || obj.p_uin || '')
    : (obj.uin || obj.qqmusic_uin || obj.wxuin || obj.p_uin || '');
  const uin = String(rawUin).replace(/\D/g, '');
  const musicKey = obj.qm_keyst || obj.qqmusic_key || obj.music_key || obj.p_skey || obj.skey ||
    obj.psrf_qqaccess_token || obj.psrf_qqrefresh_token || obj.wxrefresh_token || obj.wxskey || '';
  return !!(uin && musicKey);
}

function qqCookieHasPlaybackLogin(cookieText) {
  const obj = parseCookieHeader(cookieText);
  const rawUin = Number(obj.login_type) === 2
    ? (obj.wxuin || obj.uin || obj.p_uin || '')
    : (obj.uin || obj.qqmusic_uin || obj.wxuin || obj.p_uin || '');
  const uin = String(rawUin).replace(/\D/g, '');
  const playbackKey = obj.qm_keyst || obj.qqmusic_key || obj.music_key || obj.wxskey || '';
  return !!(uin && playbackKey);
}

function neteaseCookieHasLogin(cookieText) {
  const obj = parseCookieHeader(cookieText);
  return !!obj.MUSIC_U;
}

function isQQCookieDomain(domain) {
  const normalized = String(domain || '').replace(/^\./, '').toLowerCase();
  return normalized === 'qq.com' || normalized.endsWith('.qq.com') || normalized.endsWith('qqmusic.qq.com');
}

function isNeteaseCookieDomain(domain) {
  const normalized = String(domain || '').replace(/^\./, '').toLowerCase();
  return normalized === '163.com' || normalized.endsWith('.163.com') ||
    normalized === 'music.163.com' || normalized.endsWith('.music.163.com') ||
    normalized === 'netease.com' || normalized.endsWith('.netease.com');
}

function buildCookieHeaderFor(cookies, isAllowedDomain, priority) {
  const picked = new Map();
  (cookies || []).forEach((cookie) => {
    if (!cookie || !cookie.name || !isAllowedDomain(cookie.domain)) return;
    picked.set(cookie.name, cookie.value || '');
  });

  const ordered = [];
  (priority || []).forEach((name) => {
    if (picked.has(name)) {
      ordered.push([name, picked.get(name)]);
      picked.delete(name);
    }
  });
  picked.forEach((value, name) => ordered.push([name, value]));

  return ordered
    .filter(([name, value]) => name && value != null && String(value) !== '')
    .map(([name, value]) => `${name}=${value}`)
    .join('; ');
}

function buildCookieHeader(cookies) {
  return buildCookieHeaderFor(cookies, isQQCookieDomain, QQ_LOGIN_COOKIE_PRIORITY);
}

async function readQQLoginCookieHeader(cookieSession) {
  const cookies = await cookieSession.cookies.get({});
  return buildCookieHeader(cookies);
}

async function readNeteaseLoginCookieHeader(cookieSession) {
  const cookies = await cookieSession.cookies.get({});
  return buildCookieHeaderFor(cookies, isNeteaseCookieDomain, NETEASE_LOGIN_COOKIE_PRIORITY);
}

async function openNeteaseMusicLoginWindow(owner) {
  const cookieSession = session.fromPartition(NETEASE_LOGIN_PARTITION);
  const initialCookie = await readNeteaseLoginCookieHeader(cookieSession);
  if (neteaseCookieHasLogin(initialCookie)) return { ok: true, cookie: initialCookie, reused: true };

  return new Promise((resolve) => {
    let settled = false;
    let pollTimer = null;

    const loginWindow = new BrowserWindow({
      width: 940,
      height: 760,
      minWidth: 780,
      minHeight: 580,
      parent: owner && !owner.isDestroyed() ? owner : undefined,
      modal: false,
      show: false,
      autoHideMenuBar: true,
      title: '网易云音乐登录',
      backgroundColor: '#111111',
      icon: APP_ICON_ICO,
      webPreferences: {
        partition: NETEASE_LOGIN_PARTITION,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
    });

    const finish = async (result) => {
      if (settled) return;
      settled = true;
      if (pollTimer) clearInterval(pollTimer);
      if (loginWindow && !loginWindow.isDestroyed()) {
        loginWindow.close();
      }
      resolve(result);
    };

    const checkCookies = async () => {
      try {
        const cookie = await readNeteaseLoginCookieHeader(cookieSession);
        if (neteaseCookieHasLogin(cookie)) {
          finish({ ok: true, cookie });
        }
      } catch (e) {
        console.warn('Netease login cookie check failed:', e.message);
      }
    };

    loginWindow.webContents.setWindowOpenHandler(({ url }) => {
      if (/^https?:\/\/([^/]+\.)?(163|music\.163|netease)\.com/i.test(url)) {
        loginWindow.loadURL(url).catch((e) => console.warn('Netease login popup navigation failed:', e.message));
      } else if (/^https?:\/\//i.test(url)) {
        shell.openExternal(url).catch(() => {});
      }
      return { action: 'deny' };
    });

    loginWindow.webContents.on('did-finish-load', () => {
      checkCookies();
      loginWindow.webContents.executeJavaScript(`
        setTimeout(() => {
          const docs = [document];
          document.querySelectorAll('iframe').forEach((frame) => {
            try { if (frame.contentDocument) docs.push(frame.contentDocument); } catch (_) {}
          });
          for (const doc of docs) {
            const nodes = Array.from(doc.querySelectorAll('a, button, span, div'));
            const loginNode = nodes.find((node) => {
              const text = (node.textContent || '').trim();
              if (!/登录|立即登录/.test(text)) return false;
              const rect = node.getBoundingClientRect();
              return rect.width > 0 && rect.height > 0;
            });
            if (loginNode) { loginNode.click(); return true; }
          }
          return false;
        }, 900);
      `, true).catch(() => {});
    });

    loginWindow.on('ready-to-show', () => loginWindow.show());
    loginWindow.on('closed', async () => {
      if (settled) return;
      if (pollTimer) clearInterval(pollTimer);
      try {
        const cookie = await readNeteaseLoginCookieHeader(cookieSession);
        resolve(neteaseCookieHasLogin(cookie)
          ? { ok: true, cookie, partial: !qqCookieHasPlaybackLogin(cookie) }
          : { ok: false, cancelled: true, message: '网易云登录窗口已关闭' });
      } catch (e) {
        resolve({ ok: false, error: e.message || '网易云登录窗口已关闭' });
      }
    });

    pollTimer = setInterval(checkCookies, 1200);
    loginWindow.loadURL(NETEASE_LOGIN_URL).catch((e) => finish({ ok: false, error: e.message }));
  });
}

async function openQQMusicLoginWindow(owner, options) {
  options = options || {};
  const cookieSession = session.fromPartition(QQ_LOGIN_PARTITION);
  if (options.forceRefresh) {
    await clearQQMusicLoginSession();
  } else {
    const initialCookie = await readQQLoginCookieHeader(cookieSession);
    if (qqCookieHasPlaybackLogin(initialCookie)) return { ok: true, cookie: initialCookie, reused: true };
  }

  return new Promise((resolve) => {
    let settled = false;
    let pollTimer = null;
    let warmupStarted = false;

    const loginWindow = new BrowserWindow({
      width: 900,
      height: 720,
      minWidth: 760,
      minHeight: 560,
      parent: owner && !owner.isDestroyed() ? owner : undefined,
      modal: false,
      show: false,
      autoHideMenuBar: true,
      title: 'QQ 音乐登录',
      backgroundColor: '#111111',
      icon: APP_ICON_ICO,
      webPreferences: {
        partition: QQ_LOGIN_PARTITION,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
    });

    const finish = async (result) => {
      if (settled) return;
      settled = true;
      if (pollTimer) clearInterval(pollTimer);
      if (loginWindow && !loginWindow.isDestroyed()) {
        loginWindow.close();
      }
      resolve(result);
    };

    const checkCookies = async () => {
      try {
        const cookie = await readQQLoginCookieHeader(cookieSession);
        if (qqCookieHasPlaybackLogin(cookie)) {
          finish({ ok: true, cookie });
        } else if (qqCookieHasLogin(cookie) && !warmupStarted) {
          warmupStarted = true;
          setTimeout(() => {
            if (!settled && loginWindow && !loginWindow.isDestroyed()) {
              loginWindow.loadURL('https://y.qq.com/n/ryqq/player').catch((e) => console.warn('QQ login warmup navigation failed:', e.message));
            }
          }, 900);
        }
      } catch (e) {
        console.warn('QQ login cookie check failed:', e.message);
      }
    };

    loginWindow.webContents.setWindowOpenHandler(({ url }) => {
      if (/^https?:\/\//i.test(url)) {
        loginWindow.loadURL(url).catch((e) => console.warn('QQ login popup navigation failed:', e.message));
      } else {
        shell.openExternal(url).catch(() => {});
      }
      return { action: 'deny' };
    });

    loginWindow.webContents.on('did-finish-load', () => {
      checkCookies();
      loginWindow.webContents.executeJavaScript(`
        setTimeout(() => {
          const nodes = Array.from(document.querySelectorAll('a, button, span, div'));
          const loginNode = nodes.find((node) => {
            const text = (node.textContent || '').trim();
            if (!/登录|登陆/.test(text)) return false;
            const rect = node.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
          });
          if (loginNode) loginNode.click();
        }, 700);
      `, true).catch(() => {});
    });

    loginWindow.on('ready-to-show', () => loginWindow.show());
    loginWindow.on('closed', async () => {
      if (settled) return;
      if (pollTimer) clearInterval(pollTimer);
      try {
        const cookie = await readQQLoginCookieHeader(cookieSession);
        resolve(qqCookieHasLogin(cookie)
          ? { ok: true, cookie }
          : { ok: false, cancelled: true, message: 'QQ 登录窗口已关闭' });
      } catch (e) {
        resolve({ ok: false, error: e.message || 'QQ 登录窗口已关闭' });
      }
    });

    pollTimer = setInterval(checkCookies, 1200);
    loginWindow.loadURL(QQ_LOGIN_URL).catch((e) => finish({ ok: false, error: e.message }));
  });
}

async function clearQQMusicLoginSession() {
  const cookieSession = session.fromPartition(QQ_LOGIN_PARTITION);
  await cookieSession.clearStorageData({
    storages: ['cookies', 'localstorage', 'indexdb', 'cachestorage'],
  });
  return { ok: true };
}

async function clearNeteaseMusicLoginSession() {
  const cookieSession = session.fromPartition(NETEASE_LOGIN_PARTITION);
  await cookieSession.clearStorageData({
    storages: ['cookies', 'localstorage', 'indexdb', 'cachestorage'],
  });
  return { ok: true };
}

function getWindowedBounds(win) {
  const display = win && !win.isDestroyed()
    ? screen.getDisplayMatching(win.getBounds())
    : screen.getPrimaryDisplay();
  const area = display.workArea;
  const basis = display.bounds || area;
  const maxWidth = Math.max(640, area.width - WINDOWED_MARGIN);
  const maxHeight = Math.max(360, area.height - WINDOWED_MARGIN);

  let width = Math.round(basis.width * WINDOWED_SCALE);
  let height = Math.round(width / WINDOWED_ASPECT);
  const scaledHeight = Math.round(basis.height * WINDOWED_SCALE);

  if (height > scaledHeight) {
    height = scaledHeight;
    width = Math.round(height * WINDOWED_ASPECT);
  }

  if (width < MIN_WINDOWED_WIDTH && maxWidth >= MIN_WINDOWED_WIDTH && maxHeight >= MIN_WINDOWED_HEIGHT) {
    width = MIN_WINDOWED_WIDTH;
    height = MIN_WINDOWED_HEIGHT;
  }

  if (width > maxWidth) {
    width = maxWidth;
    height = Math.round(width / WINDOWED_ASPECT);
  }
  if (height > maxHeight) {
    height = maxHeight;
    width = Math.round(height * WINDOWED_ASPECT);
  }

  width = Math.round(width);
  height = Math.round(height);

  return {
    x: Math.round(area.x + (area.width - width) / 2),
    y: Math.round(area.y + (area.height - height) / 2),
    width,
    height,
  };
}

function applyWindowedBounds(win, preferredBounds = null) {
  if (!win || win.isDestroyed()) return;
  if (win.isMaximized()) win.unmaximize();
  win.setMinimumSize(MIN_WINDOWED_WIDTH, MIN_WINDOWED_HEIGHT);
  const saved = readDesktopWindowState();
  const display = screen.getDisplayMatching(win.getBounds());
  const base = nonFullscreenWindowBounds(
    preferredBounds || saved.normalBounds || saved.bounds || getWindowedBounds(win),
    win,
    display,
  );
  win.setBounds(centerWindowBoundsOnDisplay(base, display), false);
  writeDesktopWindowStateFor(win);
  sendWindowState(win);
}

function restoreCenteredWindowBounds(win) {
  if (!win || win.isDestroyed()) return null;
  const saved = readDesktopWindowState();
  const display = screen.getDisplayMatching(win.getBounds());
  const base = nonFullscreenWindowBounds(mainWindowSnapRestoreBounds || saved.normalBounds || saved.bounds || getWindowedBounds(win), win, display);
  const bounds = centerWindowBoundsOnDisplay(base, display);
  mainWindowSnapRestoreBounds = null;
  if (win.isMaximized()) win.unmaximize();
  win.setMinimumSize(MIN_WINDOWED_WIDTH, MIN_WINDOWED_HEIGHT);
  win.setBounds(bounds, false);
  writeDesktopWindowStateFor(win);
  sendWindowState(win);
  return bounds;
}

function toggleNativeMaximize(win) {
  if (!win || win.isDestroyed() || win.isFullScreen()) return;
  if (win.isMaximized()) {
    restoreCenteredWindowBounds(win);
  } else {
    writeDesktopWindowStateFor(win);
    win.maximize();
    sendWindowState(win);
  }
}

function rememberPreFullscreenBounds(win) {
  if (!win || win.isDestroyed()) return null;
  if (mainWindowStateSaveTimer) {
    clearTimeout(mainWindowStateSaveTimer);
    mainWindowStateSaveTimer = null;
  }
  const saved = readDesktopWindowState();
  const display = screen.getDisplayMatching(win.getBounds());
  const current = win.isMaximized()
    ? (mainWindowSnapRestoreBounds || saved.normalBounds || saved.bounds)
    : win.getBounds();
  const bounds = nonFullscreenWindowBounds(current || saved.normalBounds || saved.bounds || getWindowedBounds(win), win, display);
  mainWindowPreFullscreenBounds = clampWindowBoundsToDisplays(bounds) || getWindowedBounds(win);
  return mainWindowPreFullscreenBounds;
}

function applyFullscreenRestoreBounds(win) {
  if (!win || win.isDestroyed()) return;
  const restoreBounds = mainWindowPreFullscreenBounds;
  mainWindowPreFullscreenBounds = null;
  mainWindowSnapRestoreBounds = null;
  applyWindowedBounds(win, restoreBounds);
}

function exitFullscreenToWindow(win) {
  if (!win || win.isDestroyed()) return;
  windowFullscreenActive = false;

  if (!win.isFullScreen()) {
    applyFullscreenRestoreBounds(win);
    return;
  }

  let applied = false;
  const applyOnce = () => {
    if (applied || !win || win.isDestroyed() || win.isFullScreen()) return;
    applied = true;
    applyFullscreenRestoreBounds(win);
  };

  win.once('leave-full-screen', () => setTimeout(applyOnce, 50));
  win.setFullScreen(false);
  setTimeout(applyOnce, 500);
}

function toggleFullscreen(win) {
  if (!win || win.isDestroyed()) return;
  if (win.isFullScreen() || windowFullscreenActive) {
    exitFullscreenToWindow(win);
    return;
  }
  rememberPreFullscreenBounds(win);
  windowFullscreenActive = true;
  win.setFullScreen(true);
  sendWindowState(win);
}

function overlayUrl(page) {
  const port = mainServerPort || process.env.PORT || 3000;
  return `http://127.0.0.1:${port}/${page}`;
}

function localOverlayUrl(page) {
  const [pathname, query = ''] = String(page || '').split('?');
  const filePath = path.join(__dirname, '..', 'public', pathname || 'index.html');
  const base = pathToFileURL(filePath).href;
  return query ? `${base}?${query}` : base;
}

function clampNumber(value, min, max, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function desktopLyricsScale(payload = desktopLyricsState) {
  return clampNumber(payload && payload.size, 0.72, 1.55, 1);
}

function desktopLyricsBoundsStatePath() {
  return path.join(app.getPath('userData'), 'desktop-lyrics-state.json');
}

function normalizeDesktopLyricsUserBounds(bounds) {
  if (!bounds || typeof bounds !== 'object') return null;
  const next = {
    x: Math.round(Number(bounds.x)),
    y: Math.round(Number(bounds.y)),
    width: Math.round(Number(bounds.width)),
    height: Math.round(Number(bounds.height)),
  };
  if (!Object.values(next).every(Number.isFinite)) return null;
  if (next.width < 320 || next.height < 180) return null;
  return next;
}

function normalizeDesktopLyricsRelativeBounds(bounds) {
  if (!bounds || typeof bounds !== 'object') return null;
  const next = {
    x: Number(bounds.x),
    y: Number(bounds.y),
    width: Number(bounds.width),
    height: Number(bounds.height),
  };
  if (!Object.values(next).every(Number.isFinite)) return null;
  if (next.width <= 0 || next.height <= 0) return null;
  return next;
}

function desktopLyricsRelativeBounds(bounds, displayBounds) {
  const normalized = normalizeDesktopLyricsUserBounds(bounds);
  const area = normalizeDesktopLyricsUserBounds(displayBounds);
  if (!normalized || !area) return null;
  return {
    x: (normalized.x - area.x) / area.width,
    y: (normalized.y - area.y) / area.height,
    width: normalized.width / area.width,
    height: normalized.height / area.height,
  };
}

function desktopLyricsBoundsFromRelative(relativeBounds, displayBounds) {
  const relative = normalizeDesktopLyricsRelativeBounds(relativeBounds);
  const area = normalizeDesktopLyricsUserBounds(displayBounds);
  if (!relative || !area) return null;
  return normalizeDesktopLyricsUserBounds({
    x: area.x + relative.x * area.width,
    y: area.y + relative.y * area.height,
    width: Math.max(320, relative.width * area.width),
    height: Math.max(180, relative.height * area.height),
  });
}

function desktopLyricsBoundsIntersectDisplay(bounds, display) {
  const normalized = normalizeDesktopLyricsUserBounds(bounds);
  const area = display && display.bounds;
  if (!normalized || !area) return false;
  const width = Math.max(0, Math.min(normalized.x + normalized.width, area.x + area.width) - Math.max(normalized.x, area.x));
  const height = Math.max(0, Math.min(normalized.y + normalized.height, area.y + area.height) - Math.max(normalized.y, area.y));
  return width > 0 && height > 0;
}

function desktopLyricsTargetDisplay(raw, savedBounds) {
  const displays = screen.getAllDisplays();
  const savedDisplayId = raw && raw.displayId != null ? String(raw.displayId) : '';
  const byId = displays.find((display) => String(display.id) === savedDisplayId);
  if (byId) return byId;
  if (savedBounds) {
    const byBounds = screen.getDisplayMatching(savedBounds);
    if (desktopLyricsBoundsIntersectDisplay(savedBounds, byBounds)) return byBounds;
  }
  const savedDisplayBounds = normalizeDesktopLyricsUserBounds(raw && raw.displayBounds);
  if (savedDisplayBounds) {
    const byDisplayBounds = screen.getDisplayMatching(savedDisplayBounds);
    if (desktopLyricsBoundsIntersectDisplay(savedDisplayBounds, byDisplayBounds)) return byDisplayBounds;
  }
  return screen.getPrimaryDisplay();
}

function readDesktopLyricsUserBounds() {
  try {
    const raw = JSON.parse(fs.readFileSync(desktopLyricsBoundsStatePath(), 'utf8'));
    const savedBounds = normalizeDesktopLyricsUserBounds(raw && (raw.bounds || raw));
    const targetDisplay = desktopLyricsTargetDisplay(raw, savedBounds);
    let relativeBounds = normalizeDesktopLyricsRelativeBounds(raw && raw.relativeBounds);
    if (!relativeBounds && savedBounds) {
      const sourceDisplayBounds = normalizeDesktopLyricsUserBounds(raw && raw.displayBounds)
        || {
          ...DESKTOP_LYRICS_REFERENCE_DISPLAY_BOUNDS,
          x: targetDisplay && targetDisplay.bounds ? targetDisplay.bounds.x : 0,
          y: targetDisplay && targetDisplay.bounds ? targetDisplay.bounds.y : 0,
        };
      relativeBounds = desktopLyricsRelativeBounds(savedBounds, sourceDisplayBounds);
    }
    if (!relativeBounds) return savedBounds;
    const adaptiveBounds = desktopLyricsBoundsFromRelative(relativeBounds, targetDisplay.bounds);
    return adaptiveBounds ? constrainDesktopLyricsBounds(adaptiveBounds) : savedBounds;
  } catch (_) {
    return null;
  }
}

function writeDesktopLyricsUserBounds(bounds = desktopLyricsUserBounds) {
  const normalized = normalizeDesktopLyricsUserBounds(bounds);
  if (!normalized) return;
  try {
    const display = screen.getDisplayMatching(normalized);
    const displayBounds = normalizeDesktopLyricsUserBounds(display && display.bounds)
      || DESKTOP_LYRICS_REFERENCE_DISPLAY_BOUNDS;
    const relativeBounds = desktopLyricsRelativeBounds(normalized, displayBounds);
    fs.mkdirSync(path.dirname(desktopLyricsBoundsStatePath()), { recursive: true });
    fs.writeFileSync(desktopLyricsBoundsStatePath(), JSON.stringify({
      bounds: normalized,
      displayId: display && display.id,
      displayBounds,
      relativeBounds,
      updatedAt: Date.now(),
    }, null, 2), 'utf8');
  } catch (e) {
    console.warn('Desktop lyrics bounds save failed:', e.message || e);
  }
}

function scheduleDesktopLyricsUserBoundsSave(delay = 180) {
  if (desktopLyricsBoundsSaveTimer) clearTimeout(desktopLyricsBoundsSaveTimer);
  desktopLyricsBoundsSaveTimer = setTimeout(() => {
    desktopLyricsBoundsSaveTimer = null;
    writeDesktopLyricsUserBounds();
  }, delay);
}

function flushDesktopLyricsUserBounds() {
  if (desktopLyricsBoundsSaveTimer) clearTimeout(desktopLyricsBoundsSaveTimer);
  desktopLyricsBoundsSaveTimer = null;
  writeDesktopLyricsUserBounds();
}

function clearDesktopLyricsUserBounds() {
  if (desktopLyricsBoundsSaveTimer) clearTimeout(desktopLyricsBoundsSaveTimer);
  desktopLyricsBoundsSaveTimer = null;
  try {
    if (fs.existsSync(desktopLyricsBoundsStatePath())) fs.unlinkSync(desktopLyricsBoundsStatePath());
  } catch (e) {
    console.warn('Desktop lyrics bounds reset failed:', e.message || e);
  }
}

function desktopLyricsWindowSize(displayBounds, payload = desktopLyricsState) {
  const scale = desktopLyricsScale(payload);
  const baseWidth = displayBounds.width * DESKTOP_LYRICS_DEFAULT_WIDTH_RATIO;
  const baseHeight = displayBounds.height * DESKTOP_LYRICS_DEFAULT_HEIGHT_RATIO;
  return {
    width: Math.round(Math.min(Math.max(520, baseWidth * scale), displayBounds.width - 48)),
    height: Math.round(Math.min(Math.max(180, baseHeight * scale), displayBounds.height - 48)),
  };
}

function desktopLyricsDefaultBounds(payload = desktopLyricsState) {
  const display = desktopLyricsUserBounds
    ? screen.getDisplayMatching(desktopLyricsUserBounds)
    : screen.getPrimaryDisplay();
  const bounds = display.bounds;
  const yRatio = clampNumber(payload.y, 0.08, 0.92, 0.76);
  const { width, height } = desktopLyricsWindowSize(bounds, payload);
  return {
    x: Math.round(bounds.x + (bounds.width - width) / 2),
    y: Math.round(bounds.y + bounds.height * yRatio - height / 2),
    width,
    height,
  };
}

function desktopLyricsScaledUserBounds(payload = desktopLyricsState) {
  if (!desktopLyricsUserBounds) return null;
  const display = screen.getDisplayMatching(desktopLyricsUserBounds);
  const area = display.bounds;
  const { width, height } = desktopLyricsWindowSize(area, payload);
  const centerX = desktopLyricsUserBounds.x + desktopLyricsUserBounds.width / 2;
  const centerY = desktopLyricsUserBounds.y + desktopLyricsUserBounds.height / 2;
  return {
    x: Math.round(centerX - width / 2),
    y: Math.round(centerY - height / 2),
    width,
    height,
  };
}

function constrainDesktopLyricsBounds(bounds) {
  const display = screen.getDisplayMatching(bounds);
  const area = display.bounds;
  const next = {
    ...bounds,
    width: Math.round(Math.min(Math.max(320, bounds.width), area.width)),
    height: Math.round(Math.min(Math.max(180, bounds.height), area.height)),
  };
  const maxX = area.x + Math.max(0, area.width - next.width);
  const maxY = area.y + Math.max(0, area.height - next.height);
  next.x = Math.round(clampNumber(next.x, area.x, maxX, area.x));
  next.y = Math.round(clampNumber(next.y, area.y, maxY, area.y));
  return next;
}

function setDesktopLyricsBounds(bounds) {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  const nextBounds = constrainDesktopLyricsBounds(bounds);
  const currentBounds = desktopLyricsWindow.getBounds();
  if (
    currentBounds.x === nextBounds.x
    && currentBounds.y === nextBounds.y
    && currentBounds.width === nextBounds.width
    && currentBounds.height === nextBounds.height
  ) {
    return;
  }
  desktopLyricsProgrammaticMove = true;
  desktopLyricsWindow.setBounds(nextBounds, false);
  setTimeout(() => {
    desktopLyricsProgrammaticMove = false;
  }, 120);
}

function rememberDesktopLyricsBounds() {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed() || desktopLyricsProgrammaticMove) return;
  desktopLyricsUserBounds = desktopLyricsWindow.getBounds();
  scheduleDesktopLyricsUserBoundsSave();
}

function applyDesktopLyricsMouseBehavior() {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  const locked = desktopLyricsState.clickThrough !== false;
  const shouldIgnore = locked || !desktopLyricsPointerCapture;
  if (desktopLyricsMouseIgnored === shouldIgnore) return;
  desktopLyricsMouseIgnored = shouldIgnore;
  if (shouldIgnore && process.platform === 'darwin') {
    desktopLyricsWindow.setIgnoreMouseEvents(true, { forward: true });
  } else {
    desktopLyricsWindow.setIgnoreMouseEvents(shouldIgnore);
  }
}

function desktopLyricsHotBoundsOnScreen() {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return null;
  const winBounds = desktopLyricsWindow.getBounds();
  const rel = desktopLyricsHotBounds;
  if (!rel) return winBounds;
  return {
    x: winBounds.x + rel.left,
    y: winBounds.y + rel.top,
    width: Math.max(1, rel.right - rel.left),
    height: Math.max(1, rel.bottom - rel.top),
  };
}

function pointInBounds(point, bounds) {
  if (!point || !bounds) return false;
  return point.x >= bounds.x
    && point.x <= bounds.x + bounds.width
    && point.y >= bounds.y
    && point.y <= bounds.y + bounds.height;
}

function handleDesktopLyricsGlobalMiddleClick() {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  if (!desktopLyricsState.enabled) return;
  const now = Date.now();
  if (now - desktopLyricsLastMiddleAt < 260) return;
  const point = screen.getCursorScreenPoint();
  if (!pointInBounds(point, desktopLyricsHotBoundsOnScreen())) return;
  desktopLyricsLastMiddleAt = now;
  const nextLocked = desktopLyricsState.clickThrough === false;
  desktopLyricsMiddleAdjustActive = !nextLocked;
  desktopLyricsState = { ...desktopLyricsState, clickThrough: nextLocked, middleAdjustActive: desktopLyricsMiddleAdjustActive };
  desktopLyricsPointerCapture = !nextLocked;
  applyDesktopLyricsMouseBehavior();
  broadcastDesktopLyricsLockState();
}

function startDesktopLyricsMousePoller() {
  if (process.platform !== 'win32' || desktopLyricsMousePoller) return;
  const script = `
$ErrorActionPreference = "SilentlyContinue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MineradioMousePoll {
  [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
}
"@
$prev = $false
while ($true) {
  $down = (([MineradioMousePoll]::GetAsyncKeyState(4) -band 0x8000) -ne 0)
  if ($down -and -not $prev) {
    [Console]::Out.WriteLine("MMB")
    [Console]::Out.Flush()
  }
  $prev = $down
  Start-Sleep -Milliseconds 24
}
`;
  try {
    desktopLyricsMousePoller = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    desktopLyricsMousePoller.stdout.on('data', (chunk) => {
      desktopLyricsMousePollerBuffer += chunk.toString('utf8');
      const lines = desktopLyricsMousePollerBuffer.split(/\r?\n/);
      desktopLyricsMousePollerBuffer = lines.pop() || '';
      lines.forEach((line) => {
        if (line.trim() === 'MMB') handleDesktopLyricsGlobalMiddleClick();
      });
    });
    desktopLyricsMousePoller.on('exit', () => {
      desktopLyricsMousePoller = null;
      desktopLyricsMousePollerBuffer = '';
    });
    desktopLyricsMousePoller.on('error', () => {
      desktopLyricsMousePoller = null;
      desktopLyricsMousePollerBuffer = '';
    });
  } catch (e) {
    desktopLyricsMousePoller = null;
    desktopLyricsMousePollerBuffer = '';
  }
}

function stopDesktopLyricsMousePoller() {
  if (!desktopLyricsMousePoller) return;
  try {
    desktopLyricsMousePoller.kill();
  } catch (e) {}
  desktopLyricsMousePoller = null;
  desktopLyricsMousePollerBuffer = '';
}

function broadcastDesktopLyricsLockState() {
  const locked = desktopLyricsState.clickThrough !== false;
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('mineradio-desktop-lyrics-lock-state', { locked });
  }
  sendDesktopLyricsState();
}

function broadcastDesktopLyricsEnabledState(enabled) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('mineradio-desktop-lyrics-enabled-state', { enabled: !!enabled });
  }
}

function broadcastWallpaperEnabledState(enabled) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('mineradio-wallpaper-enabled-state', { enabled: !!enabled });
  }
}

function setWallpaperSuspendedForMainWindow(suspended, reason = '') {
  const next = !!suspended
    && !!USE_DIRECT_ELECTRON_WALLPAPER
    && !USE_WALLPAPER_ENGINE_WEB_BRIDGE
    && !!(wallpaperWindow && !wallpaperWindow.isDestroyed && !wallpaperWindow.isDestroyed() && !wallpaperWindow.nativeWallpaper);
  if (wallpaperSuspendedForMainWindow === next) return;
  wallpaperSuspendedForMainWindow = next;
  wallpaperDebugLog('wallpaper-main-window-suspend', { suspended: next, reason });
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  try {
    wallpaperWindow.webContents.send('mineradio-wallpaper-visibility', {
      paused: next,
      reason,
      at: Date.now(),
    });
  } catch (_) {}
  if (next) {
    stopWallpaperClipController();
    try { if (wallpaperWindow.isVisible()) wallpaperWindow.hide(); } catch (_) {}
    return;
  }
  if (wallpaperState && wallpaperState.enabled) {
    showAndAttachWallpaperWindow();
    sendWallpaperStateSoon(60, { persist: false });
  }
}

function updateWallpaperMainWindowSuspension(reason = '') {
  const visiblePlayer = !!(
    mainWindow
    && !mainWindow.isDestroyed()
    && mainWindow.isVisible()
    && !mainWindow.isMinimized()
  );
  setWallpaperSuspendedForMainWindow(visiblePlayer, reason);
}

function sendWallpaperPointerState() {
  if (!wallpaperState.enabled || !wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  if (!wallpaperWindow.webContents || typeof wallpaperWindow.webContents.send !== 'function') return;
  try {
    const bounds = wallpaperWindow.getBounds();
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) return;
    const point = screen.getCursorScreenPoint();
    const clientX = clampNumber(point.x - bounds.x, 0, bounds.width, 0);
    const clientY = clampNumber(point.y - bounds.y, 0, bounds.height, 0);
    const ndcX = clampNumber(clientX / Math.max(1, bounds.width) * 2 - 1, -1, 1, 0);
    const ndcY = clampNumber(-(clientY / Math.max(1, bounds.height) * 2 - 1), -1, 1, 0);
    const key = `${Math.round(clientX)}|${Math.round(clientY)}|${wallpaperPrimaryDown ? 1 : 0}|${bounds.width}|${bounds.height}`;
    if (key === wallpaperLastPointerKey) return;
    wallpaperLastPointerKey = key;
    wallpaperWindow.webContents.send('mineradio-wallpaper-pointer', {
      clientX,
      clientY,
      ndcX,
      ndcY,
      primaryDown: !!wallpaperPrimaryDown,
      width: bounds.width,
      height: bounds.height,
      at: Date.now(),
    });
  } catch (_) {}
}

function isPointOverMainWindow(point) {
  if (!mainWindow || mainWindow.isDestroyed() || !mainWindow.isVisible() || mainWindow.isMinimized()) return false;
  return pointInBounds(point, mainWindow.getBounds());
}

function sendWallpaperInputState(payload) {
  if (!wallpaperState.enabled || !wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  if (!wallpaperWindow.webContents || typeof wallpaperWindow.webContents.send !== 'function') return;
  try { wallpaperWindow.webContents.send('mineradio-wallpaper-input', payload); } catch (_) {}
}

function handleWallpaperInputHookLine(line) {
  const parts = String(line || '').trim().split(/\s+/);
  if (!parts[0]) return;
  if (parts[0] === 'BTN') {
    wallpaperPrimaryDown = parts[1] === '1';
    wallpaperLastPointerKey = '';
    sendWallpaperPointerState();
    return;
  }
  if (parts[0] !== 'WHEEL') return;
  const wheelDelta = Number(parts[1]) || 0;
  const point = {
    x: Number(parts[2]) || 0,
    y: Number(parts[3]) || 0,
  };
  if (!wheelDelta) return;
  if (isPointOverMainWindow(point)) return;
  const bounds = wallpaperWindow && !wallpaperWindow.isDestroyed() ? wallpaperWindow.getBounds() : null;
  if (bounds && !pointInBounds(point, bounds)) return;
  sendWallpaperInputState({
    type: 'wheel',
    deltaY: -wheelDelta * (100 / 120),
    wheelDelta,
    x: point.x,
    y: point.y,
    at: Date.now(),
  });
}

function startWallpaperInputHook() {
  if (!WALLPAPER_INPUT_HOOK_ENABLED || process.platform !== 'win32' || wallpaperInputHookProcess) return;
  const script = `
$ErrorActionPreference = "SilentlyContinue"
Add-Type -ReferencedAssemblies System.Windows.Forms @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
public static class MineradioWallpaperInputHook {
  const int WH_MOUSE_LL = 14;
  const int WM_MOUSEWHEEL = 0x020A;
  const int WM_LBUTTONDOWN = 0x0201;
  const int WM_LBUTTONUP = 0x0202;
  const int WM_LBUTTONDBLCLK = 0x0203;
  const int GA_ROOT = 2;
  delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
  static LowLevelMouseProc _proc = HookCallback;
  static IntPtr _hookID = IntPtr.Zero;
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }
  [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
  [DllImport("user32.dll", SetLastError=true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
  [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] static extern IntPtr GetModuleHandle(string lpModuleName);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
  static bool IsDesktopPoint(POINT pt) {
    IntPtr hwnd = WindowFromPoint(pt);
    if (hwnd == IntPtr.Zero) return true;
    IntPtr root = GetAncestor(hwnd, GA_ROOT);
    if (root == IntPtr.Zero) root = hwnd;
    string cls = ClassName(hwnd);
    string rootCls = ClassName(root);
    return cls == "SysListView32" || cls == "SHELLDLL_DefView" || cls == "WorkerW" || cls == "Progman"
      || rootCls == "WorkerW" || rootCls == "Progman";
  }
  static string ClassName(IntPtr hwnd) {
    var sb = new StringBuilder(256);
    GetClassName(hwnd, sb, sb.Capacity);
    return sb.ToString();
  }
  public static void Run() {
    using (Process cur = Process.GetCurrentProcess())
    using (ProcessModule mod = cur.MainModule) {
      _hookID = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
      Application.Run();
      if (_hookID != IntPtr.Zero) UnhookWindowsHookEx(_hookID);
    }
  }
  static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
    if (nCode >= 0) {
      int msg = wParam.ToInt32();
      var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
      if ((msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK) && IsDesktopPoint(info.pt)) {
        Console.Out.WriteLine("BTN 1 " + info.pt.x + " " + info.pt.y);
        Console.Out.Flush();
      } else if (msg == WM_LBUTTONUP) {
        Console.Out.WriteLine("BTN 0 " + info.pt.x + " " + info.pt.y);
        Console.Out.Flush();
      } else if (msg == WM_MOUSEWHEEL && IsDesktopPoint(info.pt)) {
        short delta = unchecked((short)((info.mouseData >> 16) & 0xffff));
        Console.Out.WriteLine("WHEEL " + delta + " " + info.pt.x + " " + info.pt.y);
        Console.Out.Flush();
      }
    }
    return CallNextHookEx(_hookID, nCode, wParam, lParam);
  }
}
"@
[MineradioWallpaperInputHook]::Run()
`;
  try {
    wallpaperInputHookProcess = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    wallpaperDebugLog('input-hook-start', { pid: wallpaperInputHookProcess.pid });
    wallpaperInputHookProcess.stdout.on('data', (chunk) => {
      wallpaperInputHookBuffer += chunk.toString('utf8');
      const lines = wallpaperInputHookBuffer.split(/\r?\n/);
      wallpaperInputHookBuffer = lines.pop() || '';
      lines.forEach(handleWallpaperInputHookLine);
    });
    wallpaperInputHookProcess.stderr.on('data', (chunk) => {
      const text = chunk.toString('utf8').trim();
      if (text) wallpaperDebugLog('input-hook-stderr', { text: text.slice(0, 500) });
    });
    wallpaperInputHookProcess.on('exit', (code, signal) => {
      wallpaperDebugLog('input-hook-exit', { code, signal });
      wallpaperInputHookProcess = null;
      wallpaperInputHookBuffer = '';
      wallpaperPrimaryDown = false;
    });
    wallpaperInputHookProcess.on('error', (error) => {
      wallpaperDebugLog('input-hook-error', { error: error && error.message || String(error) });
      wallpaperInputHookProcess = null;
      wallpaperInputHookBuffer = '';
      wallpaperPrimaryDown = false;
    });
  } catch (e) {
    wallpaperInputHookProcess = null;
    wallpaperInputHookBuffer = '';
    wallpaperPrimaryDown = false;
    wallpaperDebugLog('input-hook-start-failed', { error: e.message || String(e) });
  }
}

function stopWallpaperInputHook() {
  if (!wallpaperInputHookProcess) return;
  try { wallpaperInputHookProcess.kill(); } catch (e) {}
  wallpaperInputHookProcess = null;
  wallpaperInputHookBuffer = '';
  wallpaperPrimaryDown = false;
  wallpaperLastPointerKey = '';
}

function startWallpaperMousePoller() {
  if (!WALLPAPER_POINTER_SYNC_ENABLED) return;
  if (wallpaperMousePoller) return;
  wallpaperLastPointerKey = '';
  startWallpaperInputHook();
  wallpaperMousePoller = setInterval(() => {
    try {
      if (!wallpaperState.enabled || !wallpaperWindow || wallpaperWindow.isDestroyed()) {
        stopWallpaperMousePoller();
        return;
      }
      sendWallpaperPointerState();
    } catch (e) {
      wallpaperDebugLog('pointer-poll-failed', { error: e.message || String(e) });
    }
  }, 33);
  sendWallpaperPointerState();
}

function stopWallpaperMousePoller() {
  if (wallpaperMousePoller) clearInterval(wallpaperMousePoller);
  wallpaperMousePoller = null;
  wallpaperLastPointerKey = '';
  stopWallpaperInputHook();
}

function positionDesktopLyricsWindow(payload = desktopLyricsState, options = {}) {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  const shouldUseManualBounds = desktopLyricsUserBounds && !options.force;
  setDesktopLyricsBounds(shouldUseManualBounds ? desktopLyricsUserBounds : desktopLyricsDefaultBounds(payload));
  if (typeof desktopLyricsWindow.setOpacity === 'function') {
    desktopLyricsWindow.setOpacity(clampNumber(payload.opacity, 0.28, 1, 0.92));
  }
}

function refreshDesktopLyricsBoundsForDisplays() {
  if (desktopLyricsBoundsSaveTimer) clearTimeout(desktopLyricsBoundsSaveTimer);
  desktopLyricsBoundsSaveTimer = null;
  const adaptiveBounds = readDesktopLyricsUserBounds();
  desktopLyricsUserBounds = adaptiveBounds
    || (desktopLyricsUserBounds ? constrainDesktopLyricsBounds(desktopLyricsUserBounds) : null);
  if (desktopLyricsUserBounds) scheduleDesktopLyricsUserBoundsSave();
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  positionDesktopLyricsWindow(desktopLyricsState, { force: !desktopLyricsUserBounds });
}

function splitDesktopLyricsPayload(payload = {}) {
  const persistent = payload && typeof payload === 'object' ? { ...payload } : {};
  let transientBeat = null;
  if (Object.prototype.hasOwnProperty.call(persistent, 'beatMap')) {
    transientBeat = {
      beatMapKey: persistent.beatMapKey == null ? '' : persistent.beatMapKey,
      beatMap: persistent.beatMap,
    };
    delete persistent.beatMap;
  }
  return { persistent, transientBeat };
}

function sendDesktopLyricsState(transientBeat = null) {
  if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
  const oneShotBeat = transientBeat || desktopLyricsPendingBeatPayload;
  desktopLyricsWindow.webContents.send('mineradio-desktop-lyrics-state', {
    ...desktopLyricsState,
    ...(oneShotBeat || {}),
    middleAdjustActive: desktopLyricsMiddleAdjustActive,
  });
  if (oneShotBeat && oneShotBeat === desktopLyricsPendingBeatPayload) desktopLyricsPendingBeatPayload = null;
}

function flushDesktopLyricsStateUpdate() {
  desktopLyricsUpdateTimer = null;
  const payload = desktopLyricsPendingState;
  desktopLyricsPendingState = null;
  if (!payload) return;
  desktopLyricsLastUpdateAt = Date.now();
  const splitPayload = splitDesktopLyricsPayload(payload);
  const nextState = { ...desktopLyricsState, ...splitPayload.persistent };
  if (nextState.enabled) {
    createDesktopLyricsWindow(payload);
  } else if (desktopLyricsWindow && !desktopLyricsWindow.isDestroyed()) {
    desktopLyricsState = nextState;
    sendDesktopLyricsState();
  } else {
    desktopLyricsState = nextState;
  }
}

function queueDesktopLyricsStateUpdate(payload) {
  const incoming = payload && typeof payload === 'object' ? payload : {};
  if (
    desktopLyricsPendingState
    && desktopLyricsPendingState.beatMapKey === incoming.beatMapKey
    && Object.prototype.hasOwnProperty.call(desktopLyricsPendingState, 'beatMap')
    && !Object.prototype.hasOwnProperty.call(incoming, 'beatMap')
  ) {
    desktopLyricsPendingState = { ...incoming, beatMap: desktopLyricsPendingState.beatMap };
  } else {
    desktopLyricsPendingState = incoming;
  }
  if (desktopLyricsUpdateTimer) return;
  const delay = Math.max(0, DESKTOP_LYRICS_UPDATE_INTERVAL_MS - (Date.now() - desktopLyricsLastUpdateAt));
  desktopLyricsUpdateTimer = setTimeout(flushDesktopLyricsStateUpdate, delay);
}

function clearPendingDesktopLyricsStateUpdate() {
  if (desktopLyricsUpdateTimer) clearTimeout(desktopLyricsUpdateTimer);
  desktopLyricsUpdateTimer = null;
  desktopLyricsPendingState = null;
}

function createDesktopLyricsWindow(payload = {}) {
  const splitPayload = splitDesktopLyricsPayload(payload);
  payload = splitPayload.persistent;
  if (splitPayload.transientBeat) desktopLyricsPendingBeatPayload = splitPayload.transientBeat;
  if (!desktopLyricsUserBounds) {
    desktopLyricsUserBounds = readDesktopLyricsUserBounds();
    if (desktopLyricsUserBounds) writeDesktopLyricsUserBounds();
  }
  const previousY = desktopLyricsState.y;
  const previousOpacity = desktopLyricsState.opacity;
  const previousSize = desktopLyricsState.size;
  desktopLyricsState = { ...desktopLyricsState, ...payload, enabled: true };
  if (desktopLyricsState.clickThrough !== false) desktopLyricsMiddleAdjustActive = false;
  desktopLyricsState.middleAdjustActive = desktopLyricsMiddleAdjustActive;
  const hasY = Object.prototype.hasOwnProperty.call(payload || {}, 'y');
  const hasSize = Object.prototype.hasOwnProperty.call(payload || {}, 'size');
  const nextY = clampNumber(desktopLyricsState.y, 0.08, 0.92, 0.76);
  const nextSize = desktopLyricsScale(desktopLyricsState);
  const yChanged = hasY && Number.isFinite(Number(previousY)) && Math.abs(nextY - clampNumber(previousY, 0.08, 0.92, 0.76)) > 0.001;
  const sizeChanged = hasSize && Math.abs(nextSize - clampNumber(previousSize, 0.72, 1.55, 1)) > 0.001;
  const opacityChanged = Object.prototype.hasOwnProperty.call(payload || {}, 'opacity')
    && Math.abs(clampNumber(desktopLyricsState.opacity, 0.28, 1, 0.92) - clampNumber(previousOpacity, 0.28, 1, 0.92)) > 0.001;
  if (yChanged) {
    desktopLyricsUserBounds = null;
    clearDesktopLyricsUserBounds();
  } else if (sizeChanged && desktopLyricsUserBounds) {
    desktopLyricsUserBounds = desktopLyricsScaledUserBounds(desktopLyricsState);
    scheduleDesktopLyricsUserBoundsSave();
  }
  if (desktopLyricsWindow && !desktopLyricsWindow.isDestroyed()) {
    if (yChanged || sizeChanged) {
      positionDesktopLyricsWindow(desktopLyricsState, { force: yChanged });
    } else if (opacityChanged && typeof desktopLyricsWindow.setOpacity === 'function') {
      desktopLyricsWindow.setOpacity(clampNumber(desktopLyricsState.opacity, 0.28, 1, 0.92));
    }
    applyDesktopLyricsMouseBehavior();
    sendDesktopLyricsState(splitPayload.transientBeat);
    return desktopLyricsWindow;
  }

  desktopLyricsWindow = new BrowserWindow({
    width: 920,
    height: 190,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    resizable: true,
    movable: true,
    focusable: false,
    skipTaskbar: true,
    show: false,
    title: 'Mineradio Desktop Lyrics',
    webPreferences: {
      preload: path.join(__dirname, 'overlay-preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      backgroundThrottling: false,
      webSecurity: false,
    },
  });
  try {
    desktopLyricsWindow.setAlwaysOnTop(true, 'floating', 1);
    desktopLyricsWindow.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  } catch (e) {
    console.warn('Desktop lyrics topmost setup skipped:', e.message);
  }
  startDesktopLyricsMousePoller();
  applyDesktopLyricsMouseBehavior();
  positionDesktopLyricsWindow(desktopLyricsState, { force: yChanged || !desktopLyricsUserBounds });
  desktopLyricsWindow.once('ready-to-show', () => {
    if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return;
    desktopLyricsWindow.showInactive();
    sendDesktopLyricsState();
  });
  desktopLyricsWindow.webContents.once('did-finish-load', sendDesktopLyricsState);
  desktopLyricsWindow.on('closed', () => {
    desktopLyricsWindow = null;
    desktopLyricsMouseIgnored = null;
  });
  desktopLyricsWindow.on('moved', rememberDesktopLyricsBounds);
  desktopLyricsWindow.loadURL(overlayUrl('desktop-lyrics.html')).catch((e) => console.warn('Desktop lyrics load failed:', e.message));
  return desktopLyricsWindow;
}

function closeDesktopLyricsWindow() {
  desktopLyricsState = { ...desktopLyricsState, enabled: false };
  desktopLyricsMiddleAdjustActive = false;
  if (desktopLyricsUserBounds) flushDesktopLyricsUserBounds();
  desktopLyricsPointerCapture = false;
  desktopLyricsMouseIgnored = null;
  desktopLyricsHotBounds = null;
  desktopLyricsPendingBeatPayload = null;
  stopDesktopLyricsMousePoller();
  if (desktopLyricsWindow && !desktopLyricsWindow.isDestroyed()) {
    sendDesktopLyricsState();
    desktopLyricsWindow.close();
  }
  desktopLyricsWindow = null;
  broadcastDesktopLyricsEnabledState(false);
}

function isWallpaperTextureImagePath(filePath) {
  return WALLPAPER_TEXTURE_IMAGE_EXTS.has(path.extname(String(filePath || '')).toLowerCase());
}

function isWallpaperPreviewVideoPath(filePath) {
  return WALLPAPER_PREVIEW_VIDEO_EXTS.has(path.extname(String(filePath || '')).toLowerCase());
}

function isWallpaperPreviewWebPath(filePath) {
  return WALLPAPER_PREVIEW_WEB_EXTS.has(path.extname(String(filePath || '')).toLowerCase());
}

function wallpaperPreviewTypeForPath(filePath) {
  if (!filePath) return '';
  if (isWallpaperPreviewVideoPath(filePath)) return 'video';
  if (isWallpaperPreviewWebPath(filePath)) return 'web';
  if (isWallpaperTextureImagePath(filePath)) return 'image';
  return '';
}

function normalizeWallpaperEnginePath(filePath) {
  return String(filePath || '').replace(/\\/g, '/');
}

function readJsonFileSafe(filePath) {
  try {
    if (!filePath || !fs.existsSync(filePath)) return null;
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (e) {
    return null;
  }
}

function normalizeNativeWallpaperEnginePath(filePath) {
  const raw = String(filePath || '').trim();
  if (!raw) return '';
  return path.normalize(raw.replace(/\//g, path.sep));
}

function isMineradioGeneratedWallpaperPath(filePath) {
  const normalized = normalizeWallpaperEnginePath(filePath).toLowerCase();
  return normalized.includes(`/projects/myprojects/${MINERADIO_WE_BRIDGE_PROJECT_NAME.toLowerCase()}/`)
    || normalized.includes(`/projects/myprojects/${MINERADIO_WE_OVERLAY_PROJECT_NAME.toLowerCase()}/`);
}

function wallpaperEngineMyProjectsDirFromConfig(configPath) {
  const installDir = configPath ? path.dirname(configPath) : '';
  const direct = installDir ? path.join(installDir, 'projects', 'myprojects') : '';
  if (direct && fs.existsSync(direct)) return direct;
  for (const dir of wallpaperEngineInstallDirCandidates()) {
    const candidate = path.join(dir, 'projects', 'myprojects');
    if (fs.existsSync(candidate)) return candidate;
  }
  return direct || path.join(wallpaperEngineInstallDirCandidates()[0] || app.getPath('userData'), 'projects', 'myprojects');
}

function mineradioWallpaperEngineProjectDir(configPath, projectName) {
  return path.join(wallpaperEngineMyProjectsDirFromConfig(configPath), projectName);
}

function mineradioWallpaperOverlayManifestPath(projectDir) {
  return path.join(projectDir, 'source.json');
}

function readMineradioWallpaperOverlayManifest(projectDir) {
  const manifest = readJsonFileSafe(mineradioWallpaperOverlayManifestPath(projectDir));
  return manifest && Array.isArray(manifest.items) ? manifest : null;
}

function uniqueWallpaperSourceItems(items) {
  const out = [];
  const seen = new Set();
  for (const item of items || []) {
    const normalized = normalizeWallpaperEnginePath(item);
    if (!normalized || isMineradioGeneratedWallpaperPath(normalized)) continue;
    const key = normalized.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(normalized);
  }
  return out;
}

function wallpaperSourceItemsFromSelectedItem(selectedItem, configPath) {
  if (!selectedItem || typeof selectedItem !== 'object') return null;
  const selectedFile = normalizeWallpaperEnginePath(selectedItem.file || '');
  const playlist = selectedItem.playlist && typeof selectedItem.playlist === 'object' ? selectedItem.playlist : {};
  const playlistItems = uniqueWallpaperSourceItems(Array.isArray(playlist.items) ? playlist.items : []);
  if (selectedFile && isMineradioGeneratedWallpaperPath(selectedFile)) {
    if (playlistItems.length) return { items: playlistItems, settings: playlist.settings || {}, source: 'selected-playlist' };
    const overlayDir = mineradioWallpaperEngineProjectDir(configPath, MINERADIO_WE_OVERLAY_PROJECT_NAME);
    const stored = readMineradioWallpaperOverlayManifest(overlayDir);
    if (stored && stored.items.length) {
      return {
        items: uniqueWallpaperSourceItems(stored.items.map((item) => item.originalPath || item.sourcePath || item.path)),
        settings: stored.settings || {},
        source: 'stored-overlay-manifest',
      };
    }
    return null;
  }
  const items = uniqueWallpaperSourceItems([selectedFile, ...playlistItems]);
  return items.length ? { items, settings: playlist.settings || {}, source: 'selected-wallpaper' } : null;
}

function currentWallpaperSourceItemsForOverlay() {
  for (const configPath of wallpaperEngineConfigCandidates()) {
    const config = readJsonFileSafe(configPath);
    const selectedItem = selectedWallpaperFileFromConfig(config);
    const source = wallpaperSourceItemsFromSelectedItem(selectedItem, configPath);
    if (source && source.items && source.items.length) {
      return { ...source, configPath };
    }
  }
  return null;
}

async function resolveWallpaperEnginePlayableSource(filePath, options = {}) {
  const originalPath = normalizeNativeWallpaperEnginePath(filePath);
  if (!originalPath || !fs.existsSync(originalPath)) return null;
  const stat = fs.statSync(originalPath);
  const dir = stat.isDirectory() ? originalPath : path.dirname(originalPath);
  let sourcePath = '';
  let type = wallpaperPreviewTypeForPath(originalPath);
  let source = 'direct-file';
  if (type && stat.isFile()) {
    sourcePath = originalPath;
  } else {
    const preview = resolveWallpaperPreviewFile(originalPath, dir);
    if (preview && preview.filePath) {
      sourcePath = preview.filePath;
      type = preview.type || wallpaperPreviewTypeForPath(preview.filePath);
      source = preview.source || 'project-file';
    }
  }
  if (!sourcePath || !type || !fs.existsSync(sourcePath)) return null;
  const sourceFileUrl = pathToFileURL(sourcePath).href;
  const originalFileUrl = pathToFileURL(originalPath).href;
  return {
    type,
    sourcePath,
    sourceUrl: bridgeAssetUrl(sourceFileUrl),
    sourceFileUrl,
    originalPath,
    originalUrl: originalFileUrl,
    name: path.basename(originalPath),
    source,
  };
}

function resolveWallpaperEnginePlaylistMediaFallback(selectedItem, configPath, selectedNative) {
  const source = wallpaperSourceItemsFromSelectedItem(selectedItem, configPath);
  const selectedKey = normalizeWallpaperEnginePath(selectedNative).toLowerCase();
  const items = uniqueWallpaperSourceItems(source && source.items || [])
    .filter((item) => normalizeWallpaperEnginePath(item).toLowerCase() !== selectedKey)
    .sort(() => Math.random() - 0.5);
  for (const item of items) {
    try {
      const originalPath = normalizeNativeWallpaperEnginePath(item);
      if (!originalPath || !fs.existsSync(originalPath)) continue;
      const stat = fs.statSync(originalPath);
      const dir = stat.isDirectory() ? originalPath : path.dirname(originalPath);
      let mediaPath = '';
      let mediaType = '';
      if (stat.isFile() && isWallpaperTextureImagePath(originalPath)) {
        mediaPath = originalPath;
        mediaType = 'image';
      } else if (stat.isFile() && isWallpaperPreviewVideoPath(originalPath)) {
        mediaPath = originalPath;
        mediaType = 'video';
      } else {
        const preview = resolveWallpaperPreviewFile(originalPath, dir);
        if (preview && (preview.type === 'image' || preview.type === 'video') && preview.filePath && fs.existsSync(preview.filePath)) {
          mediaPath = preview.filePath;
          mediaType = preview.type;
        }
      }
      if (!mediaPath || !fs.existsSync(mediaPath) || (mediaType !== 'image' && mediaType !== 'video')) continue;
      return {
        filePath: mediaPath,
        originalPath,
        type: mediaType,
        source: 'wallpaper_engine_playlist_media_fallback',
        playlistSource: source && source.source || '',
      };
    } catch (_) {}
  }
  return null;
}

function wallpaperOverlayPreviewPath(items, fallbackProjectDir) {
  const firstImage = (items || []).find((item) => item && item.type === 'image' && item.sourcePath && fs.existsSync(item.sourcePath));
  if (firstImage) return firstImage.sourcePath;
  const bridgePreview = path.join(path.dirname(fallbackProjectDir), MINERADIO_WE_BRIDGE_PROJECT_NAME, 'preview.jpg');
  if (fs.existsSync(bridgePreview)) return bridgePreview;
  return '';
}

function overlayProjectRelativeUrl(relativePath) {
  return String(relativePath || '')
    .split(/[\\/]+/)
    .filter(Boolean)
    .map((part) => encodeURIComponent(part))
    .join('/');
}

function copyWebWallpaperDirectoryForOverlay(item, projectDir) {
  if (!item || item.type !== 'web' || !item.sourcePath || !fs.existsSync(item.sourcePath)) return item;
  const sourceDir = path.dirname(item.sourcePath);
  const key = crypto
    .createHash('sha1')
    .update(path.normalize(sourceDir).toLowerCase())
    .digest('hex')
    .slice(0, 16);
  const rootName = path.join('original-web', key);
  const targetDir = path.join(projectDir, rootName);
  fs.mkdirSync(path.dirname(targetDir), { recursive: true });
  fs.cpSync(sourceDir, targetDir, {
    recursive: true,
    force: true,
    errorOnExist: false,
    filter: (src) => {
      const normalized = path.normalize(src).toLowerCase();
      return !normalized.includes(`${path.sep}${MINERADIO_WE_OVERLAY_PROJECT_NAME.toLowerCase()}${path.sep}`)
        && !normalized.includes(`${path.sep}${MINERADIO_WE_BRIDGE_PROJECT_NAME.toLowerCase()}${path.sep}`);
    },
  });
  const rel = path.relative(sourceDir, item.sourcePath);
  return {
    ...item,
    copiedWebRoot: targetDir,
    sourceUrl: overlayProjectRelativeUrl(path.join(rootName, rel || path.basename(item.sourcePath))),
  };
}

function prepareWallpaperOverlayItemsForProject(items, projectDir) {
  return (items || []).map((item) => {
    if (item && item.type === 'web') return copyWebWallpaperDirectoryForOverlay(item, projectDir);
    return item;
  });
}

function wallpaperOverlayIndexHtml(manifest) {
  const data = JSON.stringify(manifest).replace(/</g, '\\u003c');
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Mineradio Wallpaper Overlay</title>
<style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}
#base{position:fixed;inset:0;background:#000}
.source{position:absolute;inset:0;width:100%;height:100%;border:0;object-fit:cover;background:#000;opacity:0;transition:opacity .6s linear}
.source.active{opacity:1}
iframe.source{object-fit:unset}
#overlay{position:fixed;inset:0;width:100%;height:100%;border:0;background:transparent;pointer-events:none}
</style>
</head>
<body>
<div id="base"></div>
<iframe id="overlay" src="${manifest.stageUrl}" allow="autoplay; fullscreen"></iframe>
<script>
const manifest=${data};
const base=document.getElementById('base');
let current=null;
let index=0;
function createSource(item){
  if(!item) return null;
  let el;
  if(item.type==='video'){
    el=document.createElement('video');
    el.src=item.sourceUrl;
    el.autoplay=true; el.loop=true; el.muted=true; el.playsInline=true;
    el.addEventListener('canplay',()=>el.play().catch(()=>{}));
  }else if(item.type==='web'){
    el=document.createElement('iframe');
    el.src=item.sourceUrl;
    el.setAttribute('allow','autoplay; fullscreen');
  }else{
    el=document.createElement('img');
    el.src=item.sourceUrl;
  }
  el.className='source';
  base.appendChild(el);
  requestAnimationFrame(()=>el.classList.add('active'));
  return el;
}
function show(i){
  const items=manifest.items||[];
  if(!items.length) return;
  index=((i%items.length)+items.length)%items.length;
  const next=createSource(items[index]);
  const prev=current;
  current=next;
  if(prev){
    prev.classList.remove('active');
    setTimeout(()=>{try{prev.remove()}catch(e){}},900);
  }
}
show(0);
const delay=Math.max(15,Number(manifest.delaySeconds)||120)*1000;
if((manifest.items||[]).length>1) setInterval(()=>show(index+1),delay);
</script>
</body>
</html>
`;
}

async function ensureMineradioWallpaperEngineOverlayProject(options = {}) {
  if (!USE_WALLPAPER_ENGINE_WEB_BRIDGE) {
    return { ok: false, error: 'WALLPAPER_ENGINE_WEB_BRIDGE_DISABLED' };
  }
  const source = currentWallpaperSourceItemsForOverlay();
  if (!source || !source.items || !source.items.length) return { ok: false, error: 'WALLPAPER_ENGINE_SOURCE_NOT_FOUND' };
  const projectDir = mineradioWallpaperEngineProjectDir(source.configPath, MINERADIO_WE_OVERLAY_PROJECT_NAME);
  fs.mkdirSync(projectDir, { recursive: true });
  const items = [];
  for (const item of source.items) {
    const resolved = await resolveWallpaperEnginePlayableSource(item, { captureScene: options.captureScene !== false });
    if (resolved) items.push(resolved);
  }
  if (!items.length) return { ok: false, error: 'WALLPAPER_ENGINE_SOURCE_UNSUPPORTED', source };
  const preparedItems = prepareWallpaperOverlayItemsForProject(items, projectDir);
  const settings = source.settings || {};
  const delaySeconds = Number(settings.delay) > 0 ? Number(settings.delay) : 120;
  const manifest = {
    schema: 1,
    source: source.source || '',
    configPath: source.configPath,
    generatedAt: new Date().toISOString(),
    stageUrl: `http://127.0.0.1:${wallpaperEngineBridgePort}/stage`,
    delaySeconds,
    settings,
    items: preparedItems,
  };
  fs.writeFileSync(mineradioWallpaperOverlayManifestPath(projectDir), JSON.stringify(manifest, null, 2), 'utf8');
  fs.writeFileSync(path.join(projectDir, 'index.html'), wallpaperOverlayIndexHtml(manifest), 'utf8');
  const previewSource = wallpaperOverlayPreviewPath(items, projectDir);
  const previewName = previewSource ? `preview${path.extname(previewSource).toLowerCase() || '.jpg'}` : '';
  if (previewSource) {
    try { fs.copyFileSync(previewSource, path.join(projectDir, previewName)); } catch (_) {}
  }
  const project = {
    file: 'index.html',
    general: {
      properties: {
        schemecolor: {
          order: 0,
          text: 'ui_browse_properties_scheme_color',
          type: 'color',
          value: '0.02 0.02 0.03',
        },
      },
    },
    preview: previewName || undefined,
    tags: ['Music', 'Abstract'],
    title: 'Mineradio Overlay',
    type: 'web',
    visibility: 'private',
  };
  fs.writeFileSync(path.join(projectDir, 'project.json'), JSON.stringify(project, null, 2), 'utf8');
  wallpaperDebugLog('we-overlay-project-written', {
    projectDir,
    itemCount: items.length,
    source: source.source,
    first: items[0] && items[0].originalPath,
  });
  return {
    ok: true,
    projectDir,
    indexPath: path.join(projectDir, 'index.html'),
    manifest,
  };
}

async function activateMineradioWallpaperEngineOverlayProject(options = {}) {
  wallpaperDebugLog('we-overlay-open-blocked', { reason: 'READ_ONLY_WALLPAPER_ENGINE_SYNC' });
  return { ok: false, error: 'WALLPAPER_ENGINE_OVERLAY_DISABLED_READ_ONLY' };
}

function restoreWallpaperEngineOriginalFromOverlay() {
  wallpaperDebugLog('we-overlay-restore-blocked', { reason: 'READ_ONLY_WALLPAPER_ENGINE_SYNC' });
  return false;
}

function wallpaperEngineRunningInstallDir() {
  try {
    const out = execFileSync('powershell.exe', [
      '-NoProfile',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      '(Get-Process wallpaper64,wallpaper32 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Path)',
    ], { encoding: 'utf8', timeout: 2500, windowsHide: true }).trim();
    if (out) return path.dirname(out);
  } catch (e) {}
  return '';
}

function wallpaperEngineInstallDirCandidates() {
  const candidates = [];
  const runningDir = wallpaperEngineRunningInstallDir();
  if (runningDir) candidates.push(runningDir);
  candidates.push(
    path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Steam', 'steamapps', 'common', 'wallpaper_engine'),
    path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Steam', 'steamapps', 'common', 'wallpaper_engine'),
    'D:\\steam\\steamapps\\common\\wallpaper_engine',
    'D:\\SteamLibrary\\steamapps\\common\\wallpaper_engine',
    'E:\\SteamLibrary\\steamapps\\common\\wallpaper_engine',
    'F:\\SteamLibrary\\steamapps\\common\\wallpaper_engine'
  );
  return [...new Set(candidates.filter(Boolean).map((item) => path.resolve(item)))];
}

function wallpaperEngineExecutablePath() {
  try {
    const out = execFileSync('powershell.exe', [
      '-NoProfile',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      '(Get-Process wallpaper64,wallpaper32 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Path)',
    ], { encoding: 'utf8', timeout: 2500, windowsHide: true }).trim();
    if (out && fs.existsSync(out)) return out;
  } catch (e) {}
  for (const dir of wallpaperEngineInstallDirCandidates()) {
    const exe64 = path.join(dir, 'wallpaper64.exe');
    const exe32 = path.join(dir, 'wallpaper32.exe');
    if (fs.existsSync(exe64)) return exe64;
    if (fs.existsSync(exe32)) return exe32;
  }
  return '';
}

function wallpaperEngineConfigCandidates() {
  const runningDir = wallpaperEngineRunningInstallDir();
  const runningConfig = runningDir ? path.join(runningDir, 'config.json') : '';
  if (runningConfig && fs.existsSync(runningConfig)) return [runningConfig];
  return wallpaperEngineInstallDirCandidates()
    .map((dir) => path.join(dir, 'config.json'))
    .filter((filePath, index, all) => filePath && all.indexOf(filePath) === index);
}

function selectedWallpaperFileFromConfig(config) {
  if (!config || typeof config !== 'object') return '';
  const userKeys = Object.keys(config).filter((key) => key && key[0] !== '?' && config[key] && typeof config[key] === 'object');
  for (const userKey of userKeys) {
    const general = config[userKey] && config[userKey].general;
    const wallConfig = general && general.wallpaperconfig;
    const selected = wallConfig && wallConfig.selectedwallpapers;
    if (!selected || typeof selected !== 'object') continue;
    const item = selected.Monitor0;
    if (item && typeof item.file === 'string' && item.file.trim()) return item;
  }
  return null;
}

function wallpaperEngineSelectedKeyFromConfigPath(configPath) {
  const config = readJsonFileSafe(configPath);
  const selectedItem = selectedWallpaperFileFromConfig(config);
  const selectedFile = normalizeWallpaperEnginePath(selectedItem && selectedItem.file);
  return selectedFile ? `${path.normalize(configPath)}|${path.normalize(selectedFile)}` : '';
}

function currentWallpaperEngineSelectedKey() {
  for (const configPath of wallpaperEngineConfigCandidates()) {
    const key = wallpaperEngineSelectedKeyFromConfigPath(configPath);
    if (key) return key;
  }
  return '';
}

function applyWallpaperEngineTextureResultToState(result) {
  if (!result || !result.ok || !wallpaperState || !wallpaperState.enabled) return;
  const originalPreview = result.originalPreviewUrl || (result.wallpaperFileType ? result.wallpaperFileUrl : '');
  const originalPreviewType = result.originalPreviewType || result.wallpaperFileType || '';
  const fallbackPreview = result.fallbackPreviewUrl || result.previewUrl || result.textureUrl || result.fileUrl || '';
  const fallbackPreviewType = result.fallbackPreviewType || result.previewType || result.textureType || 'image';
  const wallpaperTexture = result.textureUrl
    || (fallbackPreviewType === 'image' ? fallbackPreview : '')
    || (originalPreviewType === 'image' ? originalPreview : '')
    || result.fileUrl
    || '';
  if (!wallpaperTexture) return;
  wallpaperState = {
    ...wallpaperState,
    wallpaperTexture,
    wallpaperTextureType: 'image',
    wallpaperTextureName: result.name || wallpaperState.wallpaperTextureName || '',
    background: normalizeWallpaperBackgroundMetrics(result.background || wallpaperState.background),
    enabled: true,
  };
  writeNativeWallpaperState();
  sendWallpaperState();
    wallpaperDebugLog('wallpaper-engine-texture-applied-to-state', {
    name: wallpaperState.wallpaperTextureName || '',
    texture: wallpaperTexture,
    background: wallpaperState.background || null,
  });
}

function sendWallpaperEngineTextureChanged(result) {
  if (!result || !mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.webContents.send('mineradio-wallpaper-engine-texture-changed', result);
}

function scheduleWallpaperEngineStartupBackgroundRefresh(reason = 'startup-refresh') {
  if (!wallpaperEngineConfigWatchAllowed()) return;
  if (!wallpaperState || !wallpaperState.enabled || !wallpaperStateNeedsBackgroundMetrics(wallpaperState)) return;
  setTimeout(async () => {
    if (!wallpaperEngineConfigWatchAllowed()) return;
    if (!wallpaperState || !wallpaperState.enabled || !wallpaperStateNeedsBackgroundMetrics(wallpaperState)) return;
    if (wallpaperEngineChangeInFlight) return;
    wallpaperEngineChangeInFlight = true;
    try {
      const result = await resolveWallpaperEngineCurrentTexture({ captureScene: false });
      wallpaperDebugLog('wallpaper-engine-startup-background-refresh', {
        reason,
        ok: !!(result && result.ok),
        name: result && result.name,
        wallpaperFile: result && result.wallpaperFile,
        background: result && result.background,
      });
      applyWallpaperEngineTextureResultToState(result);
      sendWallpaperEngineTextureChanged(result);
    } catch (e) {
      wallpaperDebugLog('wallpaper-engine-startup-background-refresh-failed', {
        reason,
        error: e.message || String(e),
      });
    } finally {
      wallpaperEngineChangeInFlight = false;
    }
  }, 900);
}

function wallpaperEngineShouldWatchConfig() {
  return !!((wallpaperState && wallpaperState.enabled) || wallpaperEnginePlayerFollowWatchEnabled);
}

function wallpaperEngineConfigWatchAllowed() {
  if (USE_WALLPAPER_ENGINE_WEB_BRIDGE) return false;
  if (!wallpaperEngineShouldWatchConfig()) return false;
  return !wallpaperEngineSyncHotPathDisabled() || wallpaperEnginePlayerFollowWatchEnabled;
}

function scheduleWallpaperEngineConfigChanged(reason = 'change') {
  if (!wallpaperEngineConfigWatchAllowed()) return;
  if (wallpaperEngineConfigWatchDebounce) clearTimeout(wallpaperEngineConfigWatchDebounce);
  wallpaperEngineConfigWatchDebounce = setTimeout(async () => {
    wallpaperEngineConfigWatchDebounce = null;
    if (!wallpaperEngineConfigWatchAllowed()) return;
    refreshWallpaperEngineConfigWatchers();
    const selectedKey = currentWallpaperEngineSelectedKey();
    if (!selectedKey || selectedKey === wallpaperEngineLastSelectedKey) return;
    wallpaperEngineLastSelectedKey = selectedKey;
    if (wallpaperEngineChangeInFlight) return;
    wallpaperEngineChangeInFlight = true;
    try {
      const result = await resolveWallpaperEngineCurrentTexture({ captureScene: false });
      wallpaperDebugLog('wallpaper-engine-config-selected-changed', {
        reason,
        ok: !!(result && result.ok),
        name: result && result.name,
        wallpaperFile: result && result.wallpaperFile,
      });
      applyWallpaperEngineTextureResultToState(result);
      sendWallpaperEngineTextureChanged(result);
    } catch (e) {
      const result = { ok: false, error: e.message || 'WALLPAPER_ENGINE_TEXTURE_RESOLVE_FAILED' };
      wallpaperDebugLog('wallpaper-engine-config-selected-change-failed', { reason, error: result.error });
      sendWallpaperEngineTextureChanged(result);
    } finally {
      wallpaperEngineChangeInFlight = false;
    }
  }, 700);
}

function refreshWallpaperEngineConfigWatchers() {
  if (process.platform !== 'win32') return;
  if (!wallpaperEngineConfigWatchAllowed()) {
    stopWallpaperEngineConfigWatchers();
    return;
  }
  const candidates = wallpaperEngineConfigCandidates().filter((filePath) => filePath && fs.existsSync(filePath));
  const wanted = new Set(candidates.map((filePath) => path.normalize(filePath)));
  for (const [filePath, watcher] of wallpaperEngineConfigWatchers) {
    if (!wanted.has(filePath)) {
      try { watcher.close(); } catch (_) {}
      wallpaperEngineConfigWatchers.delete(filePath);
    }
  }
  for (const configPath of candidates) {
    const normalized = path.normalize(configPath);
    if (wallpaperEngineConfigWatchers.has(normalized)) continue;
    try {
      const watcher = fs.watch(normalized, { persistent: false }, (_eventType) => {
        scheduleWallpaperEngineConfigChanged('fs-watch');
      });
      watcher.on('error', (e) => {
        wallpaperDebugLog('wallpaper-engine-config-watch-error', { configPath: normalized, error: e.message });
        try { watcher.close(); } catch (_) {}
        wallpaperEngineConfigWatchers.delete(normalized);
      });
      wallpaperEngineConfigWatchers.set(normalized, watcher);
      wallpaperDebugLog('wallpaper-engine-config-watch-start', { configPath: normalized });
    } catch (e) {
      wallpaperDebugLog('wallpaper-engine-config-watch-failed', { configPath: normalized, error: e.message });
    }
  }
}

function startWallpaperEngineConfigWatcher() {
  if (wallpaperEngineSyncHotPathDisabled() && !wallpaperEnginePlayerFollowWatchEnabled) {
    stopWallpaperEngineConfigWatchers();
    const now = Date.now();
    if (now - wallpaperEngineConfigWatchSkipLastAt > 10000) {
      wallpaperEngineConfigWatchSkipLastAt = now;
      wallpaperDebugLog('wallpaper-engine-config-watch-skip', { reason: 'native-d3d11' });
    }
    return;
  }
  if (!wallpaperEngineConfigWatchAllowed()) return;
  refreshWallpaperEngineConfigWatchers();
  wallpaperEngineLastSelectedKey = currentWallpaperEngineSelectedKey();
  scheduleWallpaperEngineStartupBackgroundRefresh('watch-start');
  setTimeout(refreshWallpaperEngineConfigWatchers, 5000);
  setTimeout(refreshWallpaperEngineConfigWatchers, 15000);
}

function stopWallpaperEngineConfigWatchers() {
  if (wallpaperEngineConfigWatchDebounce) {
    clearTimeout(wallpaperEngineConfigWatchDebounce);
    wallpaperEngineConfigWatchDebounce = null;
  }
  for (const watcher of wallpaperEngineConfigWatchers.values()) {
    try { watcher.close(); } catch (_) {}
  }
  wallpaperEngineConfigWatchers.clear();
}

function imageCandidateScore(filePath, baseDir) {
  const name = path.basename(filePath).toLowerCase();
  if (name === 'preview.jpg') return 1000;
  if (name === 'preview.png') return 980;
  if (name.startsWith('preview.')) return 960;
  if (name.includes('preview')) return 850;
  if (path.dirname(filePath) === baseDir) return 600;
  return 200;
}

function findImageInWallpaperDirectory(dir) {
  try {
    if (!dir || !fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) return '';
    const direct = ['preview.jpg', 'preview.png', 'preview.jpeg', 'thumbnail.jpg', 'thumbnail.png']
      .map((name) => path.join(dir, name))
      .find((filePath) => fs.existsSync(filePath) && isWallpaperTextureImagePath(filePath));
    if (direct) return direct;
    const found = [];
    const stack = [{ dir, depth: 0 }];
    while (stack.length && found.length < 80) {
      const current = stack.pop();
      for (const entry of fs.readdirSync(current.dir, { withFileTypes: true })) {
        const full = path.join(current.dir, entry.name);
        if (entry.isDirectory() && current.depth < 2 && !/^shaders?$/i.test(entry.name)) {
          stack.push({ dir: full, depth: current.depth + 1 });
        } else if (entry.isFile() && isWallpaperTextureImagePath(full)) {
          found.push(full);
        }
      }
    }
    found.sort((a, b) => {
      const score = imageCandidateScore(b, dir) - imageCandidateScore(a, dir);
      if (score) return score;
      try { return fs.statSync(b).size - fs.statSync(a).size; } catch (e) { return 0; }
    });
    return found[0] || '';
  } catch (e) {
    return '';
  }
}

function resolveWallpaperProjectFile(dir, project) {
  if (!dir || !project || typeof project !== 'object') return '';
  const candidates = [
    project.file,
    project.File,
    project.main,
    project.mainfile,
  ].filter(Boolean);
  for (const candidate of candidates) {
    const full = path.isAbsolute(String(candidate))
      ? String(candidate)
      : path.join(dir, String(candidate));
    if (fs.existsSync(full) && fs.statSync(full).isFile()) return full;
  }
  return '';
}

function findOriginalImageInWallpaperDirectory(dir) {
  try {
    if (!dir || !fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) return '';
    const found = [];
    const stack = [{ dir, depth: 0 }];
    while (stack.length && found.length < 80) {
      const current = stack.pop();
      for (const entry of fs.readdirSync(current.dir, { withFileTypes: true })) {
        const full = path.join(current.dir, entry.name);
        if (entry.isDirectory() && current.depth < 2 && !/^shaders?$/i.test(entry.name)) {
          stack.push({ dir: full, depth: current.depth + 1 });
        } else if (entry.isFile() && isWallpaperTextureImagePath(full) && !/preview|thumbnail|thumb|cover/i.test(entry.name)) {
          found.push(full);
        }
      }
    }
    found.sort((a, b) => {
      const baseA = path.dirname(a) === dir ? 1 : 0;
      const baseB = path.dirname(b) === dir ? 1 : 0;
      if (baseA !== baseB) return baseB - baseA;
      try { return fs.statSync(b).size - fs.statSync(a).size; } catch (e) { return 0; }
    });
    return found[0] || '';
  } catch (e) {
    return '';
  }
}

function findVideoInWallpaperDirectory(dir) {
  try {
    if (!dir || !fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) return '';
    const found = [];
    const stack = [{ dir, depth: 0 }];
    while (stack.length && found.length < 40) {
      const current = stack.pop();
      for (const entry of fs.readdirSync(current.dir, { withFileTypes: true })) {
        const full = path.join(current.dir, entry.name);
        if (entry.isDirectory() && current.depth < 2) stack.push({ dir: full, depth: current.depth + 1 });
        else if (entry.isFile() && isWallpaperPreviewVideoPath(full)) found.push(full);
      }
    }
    found.sort((a, b) => {
      const scoreA = /preview|sample|video|main/i.test(path.basename(a)) ? 1 : 0;
      const scoreB = /preview|sample|video|main/i.test(path.basename(b)) ? 1 : 0;
      if (scoreA !== scoreB) return scoreB - scoreA;
      try { return fs.statSync(b).size - fs.statSync(a).size; } catch (e) { return 0; }
    });
    return found[0] || '';
  } catch (e) {
    return '';
  }
}

function resolveWallpaperPreviewFile(selectedNative, dir) {
  const projectPath = fs.existsSync(selectedNative) && path.basename(selectedNative).toLowerCase() === 'project.json'
    ? selectedNative
    : path.join(dir || '', 'project.json');
  const project = readJsonFileSafe(projectPath);
  const projectFile = resolveWallpaperProjectFile(dir, project);
  if (projectFile) {
    if (isWallpaperPreviewVideoPath(projectFile)) return { filePath: projectFile, type: 'video' };
    if (isWallpaperPreviewWebPath(projectFile)) return { filePath: projectFile, type: 'web' };
    if (isWallpaperTextureImagePath(projectFile)) return { filePath: projectFile, type: 'image' };
  }
  if (fs.existsSync(selectedNative) && fs.statSync(selectedNative).isFile()) {
    if (isWallpaperPreviewVideoPath(selectedNative)) return { filePath: selectedNative, type: 'video' };
    if (isWallpaperPreviewWebPath(selectedNative)) return { filePath: selectedNative, type: 'web' };
    if (isWallpaperTextureImagePath(selectedNative)) return { filePath: selectedNative, type: 'image' };
  }
  const indexFile = path.join(dir || '', 'index.html');
  if (fs.existsSync(indexFile)) return { filePath: indexFile, type: 'web' };
  const videoFile = findVideoInWallpaperDirectory(dir);
  if (videoFile) return { filePath: videoFile, type: 'video' };
  const imageFile = findOriginalImageInWallpaperDirectory(dir);
  if (imageFile) return { filePath: imageFile, type: 'image' };
  return { filePath: '', type: '', error: 'UNSUPPORTED_WALLPAPER_ENGINE_SCENE' };
}

function cleanupWallpaperEngineCaptures(currentPath) {
  try {
    const dir = app.getPath('userData');
    fs.readdirSync(dir)
      .filter((name) => /^wallpaper-engine-capture-\d+\.png$/i.test(name))
      .map((name) => path.join(dir, name))
      .filter((filePath) => filePath !== currentPath)
      .sort((a, b) => {
        try { return fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs; } catch (e) { return 0; }
      })
      .slice(4)
      .forEach((filePath) => {
        try { fs.unlinkSync(filePath); } catch (_) {}
      });
  } catch (_) {}
}

function isLikelyBlankImage(filePath) {
  try {
    if (!filePath || !fs.existsSync(filePath)) return true;
    if (fs.statSync(filePath).size < 32000) return true;
  } catch (_) {
    return true;
  }
  return false;
}

function analyzeWallpaperBackgroundImage(filePath, source = 'wallpaper_engine_image') {
  try {
    if (!filePath || !fs.existsSync(filePath) || !isWallpaperTextureImagePath(filePath)) return null;
    const image = nativeImage.createFromPath(filePath);
    if (!image || image.isEmpty()) return null;
    const size = image.getSize();
    if (!size.width || !size.height) return null;
    const sampleWidth = 96;
    const sampleHeight = Math.max(36, Math.round(sampleWidth * size.height / Math.max(1, size.width)));
    const sample = image.resize({ width: sampleWidth, height: Math.min(96, sampleHeight) });
    const sampleSize = sample.getSize();
    const bitmap = sample.toBitmap();
    if (!bitmap || bitmap.length < sampleSize.width * sampleSize.height * 4) return null;
    let total = 0;
    let totalSq = 0;
    let edge = 0;
    let edgeCount = 0;
    const lumas = new Float32Array(sampleSize.width * sampleSize.height);
    for (let y = 0; y < sampleSize.height; y += 1) {
      for (let x = 0; x < sampleSize.width; x += 1) {
        const idx = (y * sampleSize.width + x) * 4;
        const b = bitmap[idx] / 255;
        const g = bitmap[idx + 1] / 255;
        const r = bitmap[idx + 2] / 255;
        const luma = Math.max(0, Math.min(1, 0.2126 * r + 0.7152 * g + 0.0722 * b));
        const out = y * sampleSize.width + x;
        lumas[out] = luma;
        total += luma;
        totalSq += luma * luma;
        if (x > 0) {
          edge += Math.abs(luma - lumas[out - 1]);
          edgeCount += 1;
        }
        if (y > 0) {
          edge += Math.abs(luma - lumas[out - sampleSize.width]);
          edgeCount += 1;
        }
      }
    }
    const count = Math.max(1, sampleSize.width * sampleSize.height);
    const mean = total / count;
    const variance = Math.max(0, totalSq / count - mean * mean);
    const contrast = Math.min(1, Math.sqrt(variance) * 2.2);
    const busy = Math.min(1, (edge / Math.max(1, edgeCount)) * 4.6);
    return normalizeWallpaperBackgroundMetrics({
      luma: mean,
      contrast,
      busy,
      confidence: 1,
      source,
    });
  } catch (e) {
    wallpaperDebugLog('wallpaper-background-analyze-failed', {
      filePath,
      error: e.message || String(e),
    });
    return null;
  }
}

function wallpaperEngineCaptureCachePath(wallpaperFile) {
  const key = crypto
    .createHash('sha1')
    .update(path.normalize(String(wallpaperFile || '')).toLowerCase())
    .digest('hex')
    .slice(0, 16);
  return path.join(app.getPath('userData'), `wallpaper-engine-capture-cache-${key}.png`);
}

function cachedWallpaperEngineCapture(wallpaperFile) {
  const filePath = wallpaperEngineCaptureCachePath(wallpaperFile);
  if (!filePath || !fs.existsSync(filePath) || isLikelyBlankImage(filePath)) return null;
  const info = {
    path: filePath,
    wallpaperFile,
    cached: true,
  };
  wallpaperDebugLog('wallpaper-engine-capture-cache-hit', info);
  return {
    filePath,
    type: 'image',
    source: 'wallpaper_engine_capture_cache',
    capture: info,
  };
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function runWallpaperEngineControl(args) {
  if (String(args && args[0] || '').toLowerCase() === 'openwallpaper') {
    wallpaperDebugLog('wallpaper-engine-control-blocked', { args, reason: 'READ_ONLY_WALLPAPER_ENGINE_SYNC' });
    return false;
  }
  const exe = wallpaperEngineExecutablePath();
  if (!exe) return false;
  try {
    execFile(exe, ['-control', ...args], { windowsHide: true }, () => {});
    return true;
  } catch (e) {
    wallpaperDebugLog('wallpaper-engine-control-failed', { args, error: e.message });
    return false;
  }
}

async function captureWallpaperEnginePlayInWindowSnapshot(wallpaperFile) {
  if (!desktopCapturer || process.platform !== 'win32' || !wallpaperFile || !fs.existsSync(wallpaperFile)) return null;
  const cached = cachedWallpaperEngineCapture(wallpaperFile);
  if (cached) return cached;
  const title = `Mineradio Wallpaper Capture ${Date.now()}`;
  const targetPath = wallpaperEngineCaptureCachePath(wallpaperFile);
  const opened = runWallpaperEngineControl([
    'openWallpaper',
    '-file', wallpaperFile,
    '-playInWindow', title,
    '-width', '1280',
    '-height', '720',
    '-x', '32',
    '-y', '32',
  ]);
  if (!opened) return null;
  try {
    await delay(2400);
    const sources = await desktopCapturer.getSources({
      types: ['window'],
      thumbnailSize: { width: 1280, height: 720 },
      fetchWindowIcons: false,
    });
    const source = sources.find((item) => String(item.name || '') === title)
      || sources.find((item) => String(item.name || '').includes(title));
    if (!source || !source.thumbnail || source.thumbnail.isEmpty()) {
      wallpaperDebugLog('wallpaper-engine-play-window-source-missing', {
        title,
        sources: sources.map((item) => ({ id: item.id, name: item.name })).slice(0, 30),
      });
      return null;
    }
    const rawSize = source.thumbnail.getSize();
    const titleBarCrop = Math.min(36, Math.max(24, Math.round(rawSize.height * 0.042)));
    const cropped = source.thumbnail.crop({
      x: 0,
      y: titleBarCrop,
      width: rawSize.width,
      height: Math.max(1, rawSize.height - titleBarCrop),
    });
    fs.writeFileSync(targetPath, cropped.toPNG());
    if (isLikelyBlankImage(targetPath)) {
      try { fs.unlinkSync(targetPath); } catch (_) {}
      wallpaperDebugLog('wallpaper-engine-play-window-capture-blank', { title, id: source.id, name: source.name });
      return null;
    }
    cleanupWallpaperEngineCaptures(targetPath);
    const size = cropped.getSize();
    const info = {
      path: targetPath,
      width: size.width,
      height: size.height,
      id: source.id,
      name: source.name,
      wallpaperFile,
    };
    wallpaperDebugLog('wallpaper-engine-play-window-captured', info);
    return {
      filePath: targetPath,
      type: 'image',
      source: 'wallpaper_engine_play_in_window_capture',
      capture: info,
    };
  } catch (e) {
    try { fs.unlinkSync(targetPath); } catch (_) {}
    wallpaperDebugLog('wallpaper-engine-play-window-capture-failed', { title, error: e.message });
    return null;
  } finally {
    runWallpaperEngineControl(['closeWallpaper', '-location', title]);
  }
}

async function captureWallpaperEngineDesktopSourceSnapshot() {
  if (!desktopCapturer || process.platform !== 'win32') return null;
  const targetPath = path.join(app.getPath('userData'), `wallpaper-engine-capture-${Date.now()}.png`);
  try {
    const sources = await desktopCapturer.getSources({
      types: ['window', 'screen'],
      thumbnailSize: { width: 2560, height: 1440 },
      fetchWindowIcons: false,
    });
    const windowSources = sources.filter((source) => /^window:/i.test(String(source.id || '')));
    let source = windowSources.find((item) => /wallpaper|wpe|engine/i.test(String(item.name || '')));
    if (!source) {
      source = windowSources.find((item) => /wallpaper64|wallpaper32/i.test(String(item.id || '')));
    }
    if (!source) {
      wallpaperDebugLog('wallpaper-engine-desktop-capturer-sources', {
        sources: sources.map((item) => ({ id: item.id, name: item.name })).slice(0, 30),
      });
      return null;
    }
    const image = source.thumbnail;
    if (!image || image.isEmpty()) return null;
    fs.writeFileSync(targetPath, image.toPNG());
    if (isLikelyBlankImage(targetPath)) {
      try { fs.unlinkSync(targetPath); } catch (_) {}
      wallpaperDebugLog('wallpaper-engine-desktop-capture-blank', { id: source.id, name: source.name });
      return null;
    }
    cleanupWallpaperEngineCaptures(targetPath);
    const size = image.getSize();
    const info = {
      path: targetPath,
      width: size.width,
      height: size.height,
      id: source.id,
      name: source.name,
    };
    wallpaperDebugLog('wallpaper-engine-desktop-captured', info);
    return {
      filePath: targetPath,
      type: 'image',
      source: 'wallpaper_engine_desktop_capturer',
      capture: info,
    };
  } catch (e) {
    try { fs.unlinkSync(targetPath); } catch (_) {}
    wallpaperDebugLog('wallpaper-engine-desktop-capture-failed', { error: e.message });
    return null;
  }
}

function captureWallpaperEngineWindowSnapshot() {
  if (process.platform !== 'win32') return null;
  const targetPath = path.join(app.getPath('userData'), `wallpaper-engine-capture-${Date.now()}.png`);
  const script = `
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
if (-not ("MineradioWindowCapture" -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class MineradioWindowCapture {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSource, int xSrc, int ySrc, int rop);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
}
function Get-Text([IntPtr]$hwnd, [string]$kind) {
  $sb = New-Object System.Text.StringBuilder 512
  if ($kind -eq "class") { [void][MineradioWindowCapture]::GetClassName($hwnd, $sb, $sb.Capacity) }
  else { [void][MineradioWindowCapture]::GetWindowText($hwnd, $sb, $sb.Capacity) }
  $sb.ToString()
}
$wallpaperPids = @(Get-Process wallpaper32,wallpaper64 -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
$seen = New-Object "System.Collections.Generic.HashSet[Int64]"
$wins = New-Object "System.Collections.Generic.List[object]"
$enum = $null
$enum = [MineradioWindowCapture+EnumWindowsProc]{
  param([IntPtr]$hwnd, [IntPtr]$lparam)
  $key = $hwnd.ToInt64()
  if (-not $seen.Add($key)) { return $true }
  $rect = New-Object MineradioWindowCapture+RECT
  if ([MineradioWindowCapture]::GetWindowRect($hwnd, [ref]$rect)) {
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -ge 240 -and $h -ge 160) {
      $procId = [uint32]0
      [void][MineradioWindowCapture]::GetWindowThreadProcessId($hwnd, [ref]$procId)
      $class = Get-Text $hwnd "class"
      $title = Get-Text $hwnd "title"
      $fromWallpaperProc = $wallpaperPids -contains $procId
      $looksLikeWallpaper = ($class -match "WPE|Wallpaper|WorkerW") -or ($title -match "Wallpaper")
      if ($fromWallpaperProc -or $looksLikeWallpaper) {
        $wins.Add([pscustomobject]@{
          Hwnd = $hwnd
          Pid = $procId
          Class = $class
          Title = $title
          Width = $w
          Height = $h
          Area = [int64]$w * [int64]$h
          FromWallpaperProc = $fromWallpaperProc
        }) | Out-Null
      }
    }
  }
  [void][MineradioWindowCapture]::EnumChildWindows($hwnd, $enum, [IntPtr]::Zero)
  return $true
}
[void][MineradioWindowCapture]::EnumWindows($enum, [IntPtr]::Zero)
$target = $wins |
  Where-Object { $_.FromWallpaperProc -and $_.Width -ge 640 -and $_.Height -ge 360 } |
  Sort-Object Area -Descending |
  Select-Object -First 1
if (-not $target) {
  $target = $wins |
    Where-Object { $_.Class -match "WPE|Wallpaper" -and $_.Width -ge 640 -and $_.Height -ge 360 } |
    Sort-Object Area -Descending |
    Select-Object -First 1
}
if (-not $target) { throw "WALLPAPER_ENGINE_WINDOW_NOT_FOUND" }
$hwnd = $target.Hwnd
$width = [Math]::Min([int]$target.Width, 8192)
$height = [Math]::Min([int]$target.Height, 8192)
$src = [MineradioWindowCapture]::GetWindowDC($hwnd)
if ($src -eq [IntPtr]::Zero) { throw "GET_WINDOW_DC_FAILED" }
$mem = [IntPtr]::Zero
$bmp = [IntPtr]::Zero
$old = [IntPtr]::Zero
try {
  $mem = [MineradioWindowCapture]::CreateCompatibleDC($src)
  $bmp = [MineradioWindowCapture]::CreateCompatibleBitmap($src, $width, $height)
  if ($mem -eq [IntPtr]::Zero -or $bmp -eq [IntPtr]::Zero) { throw "CREATE_BITMAP_FAILED" }
  $old = [MineradioWindowCapture]::SelectObject($mem, $bmp)
  $copyOk = [MineradioWindowCapture]::BitBlt($mem, 0, 0, $width, $height, $src, 0, 0, 0x00CC0020)
  if (-not $copyOk) { throw "BITBLT_FAILED" }
  $image = [System.Drawing.Image]::FromHbitmap($bmp)
  try {
    $image.Save(${JSON.stringify(targetPath)}, [System.Drawing.Imaging.ImageFormat]::Png)
  } finally {
    $image.Dispose()
  }
} finally {
  if ($old -ne [IntPtr]::Zero -and $mem -ne [IntPtr]::Zero) { [void][MineradioWindowCapture]::SelectObject($mem, $old) }
  if ($bmp -ne [IntPtr]::Zero) { [void][MineradioWindowCapture]::DeleteObject($bmp) }
  if ($mem -ne [IntPtr]::Zero) { [void][MineradioWindowCapture]::DeleteDC($mem) }
  [void][MineradioWindowCapture]::ReleaseDC($hwnd, $src)
}
[pscustomobject]@{
  path = ${JSON.stringify(targetPath)}
  width = $width
  height = $height
  hwnd = $hwnd.ToInt64()
  class = $target.Class
  title = $target.Title
  pid = $target.Pid
} | ConvertTo-Json -Compress
`;
  try {
    const out = execFileSync('powershell.exe', [
      '-NoProfile',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      script,
    ], { encoding: 'utf8', timeout: 6000, windowsHide: true }).trim();
    const info = JSON.parse(out);
    if (!info || !info.path || !fs.existsSync(info.path)) return null;
    if (isLikelyBlankImage(info.path)) {
      try { fs.unlinkSync(info.path); } catch (_) {}
      wallpaperDebugLog('wallpaper-engine-window-capture-blank', info);
      return null;
    }
    cleanupWallpaperEngineCaptures(info.path);
    wallpaperDebugLog('wallpaper-engine-window-captured', info);
    return {
      filePath: info.path,
      type: 'image',
      source: 'wallpaper_engine_window_capture',
      capture: info,
    };
  } catch (e) {
    try { fs.unlinkSync(targetPath); } catch (_) {}
    wallpaperDebugLog('wallpaper-engine-window-capture-failed', { error: e.message });
    return null;
  }
}

async function resolveWallpaperEngineCurrentTexture(options = {}) {
  const captureScene = WALLPAPER_ENGINE_AUTO_CAPTURE_ENABLED
    && options.captureScene === true
    && options.manual === true;
  for (const configPath of wallpaperEngineConfigCandidates()) {
    const config = readJsonFileSafe(configPath);
    const selectedItem = selectedWallpaperFileFromConfig(config);
    const selectedFile = normalizeWallpaperEnginePath(selectedItem && selectedItem.file);
    if (!selectedFile) continue;
    const selectedNative = path.normalize(selectedFile);
    let textureFile = '';
    const selectedExists = fs.existsSync(selectedNative);
    const dir = selectedExists && fs.statSync(selectedNative).isDirectory()
      ? selectedNative
      : path.dirname(selectedNative);
    const selectedDirectType = selectedExists && fs.statSync(selectedNative).isFile()
      ? wallpaperPreviewTypeForPath(selectedNative)
      : '';
    let playlistFallback = null;
    if (!selectedDirectType && selectedExists) {
      playlistFallback = resolveWallpaperEnginePlaylistMediaFallback(selectedItem, configPath, selectedNative);
    }
    if (playlistFallback && playlistFallback.filePath) {
      textureFile = playlistFallback.type === 'image' ? playlistFallback.filePath : '';
    } else if (fs.existsSync(selectedNative) && isWallpaperTextureImagePath(selectedNative)) {
      textureFile = selectedNative;
    } else {
      textureFile = findImageInWallpaperDirectory(dir);
    }
    let preview = playlistFallback && playlistFallback.filePath
      ? { filePath: playlistFallback.filePath, type: playlistFallback.type, source: playlistFallback.source }
      : resolveWallpaperPreviewFile(selectedNative, dir);
    if (!preview.filePath && preview.error === 'UNSUPPORTED_WALLPAPER_ENGINE_SCENE' && captureScene) {
      const capture = await captureWallpaperEnginePlayInWindowSnapshot(selectedNative)
        || await captureWallpaperEngineDesktopSourceSnapshot()
        || captureWallpaperEngineWindowSnapshot();
      if (capture && capture.filePath) preview = capture;
    }
    const currentPlayableType = selectedDirectType || (preview.filePath ? preview.type : '');
    if (currentPlayableType !== 'image' && currentPlayableType !== 'video') {
      playlistFallback = playlistFallback || resolveWallpaperEnginePlaylistMediaFallback(selectedItem, configPath, selectedNative);
      if (playlistFallback && playlistFallback.filePath) {
        textureFile = playlistFallback.type === 'image' ? playlistFallback.filePath : '';
        preview = {
          filePath: playlistFallback.filePath,
          type: playlistFallback.type,
          source: playlistFallback.source,
        };
      } else {
        textureFile = '';
      }
    }
    if ((textureFile && fs.existsSync(textureFile))
      || (preview.filePath && fs.existsSync(preview.filePath))
      || selectedExists) {
      const originalPreviewType = selectedDirectType;
      const originalPreviewUrl = originalPreviewType ? pathToFileURL(selectedNative).href : '';
      const backgroundSourcePath = textureFile || (preview.type === 'image' ? preview.filePath : '') || (originalPreviewType === 'image' ? selectedNative : '');
      const background = analyzeWallpaperBackgroundImage(backgroundSourcePath, preview.source || 'wallpaper_engine_current')
        || normalizeWallpaperBackgroundMetrics({ confidence: 0 });
      const displayName = playlistFallback && playlistFallback.originalPath
        ? path.basename(playlistFallback.originalPath)
        : path.basename(selectedNative || preview.filePath || textureFile);
      return {
        ok: true,
        filePath: textureFile || '',
        fileUrl: textureFile ? pathToFileURL(textureFile).href : '',
        type: textureFile ? 'image' : '',
        name: displayName,
        wallpaperFile: selectedNative,
        wallpaperFileUrl: selectedExists ? pathToFileURL(selectedNative).href : '',
        wallpaperFileType: originalPreviewType,
        originalPreviewUrl,
        originalPreviewType,
        fallbackPreviewUrl: preview.filePath ? pathToFileURL(preview.filePath).href : '',
        fallbackPreviewType: preview.type || '',
        previewPath: preview.filePath || '',
        previewUrl: preview.filePath ? pathToFileURL(preview.filePath).href : '',
        previewType: preview.type || '',
        texturePath: textureFile || '',
        textureUrl: textureFile ? pathToFileURL(textureFile).href : '',
        textureType: textureFile ? 'image' : '',
        error: preview.error || '',
        previewSource: preview.source || 'wallpaper_engine_file',
        capture: preview.capture || null,
        background,
        playlistFallback: playlistFallback ? {
          type: playlistFallback.type,
          source: playlistFallback.source,
          originalPath: playlistFallback.originalPath,
        } : null,
        source: 'wallpaper_engine_current',
        configPath,
      };
    }
  }
  return { ok: false, error: 'WALLPAPER_ENGINE_TEXTURE_NOT_FOUND' };
}

function nativeWindowHandleDecimal(win) {
  if (!win || typeof win.getNativeWindowHandle !== 'function') {
    wallpaperDebugLog('native-window-handle-skip', {
      reason: 'missing-getNativeWindowHandle',
      nativeWallpaper: !!(win && win.nativeWallpaper),
    });
    return '';
  }
  const handle = win.getNativeWindowHandle();
  if (!Buffer.isBuffer(handle) || handle.length < 4) {
    wallpaperDebugLog('native-window-handle-skip', {
      reason: 'invalid-handle-buffer',
      length: handle && typeof handle.length === 'number' ? handle.length : 0,
    });
    return '';
  }
  if (process.arch === 'x64') {
    if (handle.length < 8) return String(handle.readUInt32LE(0));
    return handle.readBigUInt64LE(0).toString();
  }
  return String(handle.readUInt32LE(0));
}

function attachWallpaperToWorkerW(win, done) {
  if (process.platform !== 'win32' || !win || win.isDestroyed()) return;
  const hwnd = nativeWindowHandleDecimal(win);
  if (!hwnd) {
    if (typeof done === 'function') setTimeout(() => done(false), 0);
    return;
  }
  if (wallpaperAttachedHwnd === hwnd && win.isVisible()) {
    wallpaperDebugLog('attach-refresh-zorder', { hwnd });
  }
  if (wallpaperAttachInFlight) {
    wallpaperDebugLog('attach-skip-in-flight', { hwnd });
    wallpaperAttachPending = true;
    if (typeof done === 'function') setTimeout(() => done(false), 0);
    return;
  }
  wallpaperAttachPending = false;
  wallpaperAttachInFlight = true;
  wallpaperDebugLog('attach-start', { hwnd, mode: 'transparent-progman-child' });
  const script = `
$ErrorActionPreference = "Stop"
if (-not ("MineradioWallpaperWin" -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MineradioWallpaperWin {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int count);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll", SetLastError=true)] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtr", SetLastError=true)] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
  [DllImport("user32.dll", EntryPoint="SetWindowLongPtr", SetLastError=true)] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
"@
}
$target = [IntPtr]([Int64]${hwnd})
$progman = [MineradioWallpaperWin]::FindWindow("Progman", "Program Manager")
if ($progman -eq [IntPtr]::Zero) { $progman = [MineradioWallpaperWin]::FindWindow("Progman", $null) }
if ($progman -eq [IntPtr]::Zero) { throw "Progman not found" }
$unused = [IntPtr]::Zero
[MineradioWallpaperWin]::SendMessageTimeout($progman, 0x052C, [IntPtr]::Zero, [IntPtr]::Zero, 0, 1000, [ref]$unused) | Out-Null
$desktopParent = [IntPtr]::Zero
$iconHost = [IntPtr]::Zero
$targetWpe = [IntPtr]::Zero
$progmanDefView = [MineradioWallpaperWin]::FindWindowEx($progman, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
$progmanWpe = [MineradioWallpaperWin]::FindWindowEx($progman, [IntPtr]::Zero, "WPEVideoWallpaper", $null)
if ($progmanWpe -eq [IntPtr]::Zero) { $progmanWpe = [MineradioWallpaperWin]::FindWindowEx($progman, [IntPtr]::Zero, "WPEImageWallpaper", $null) }
if ($progmanWpe -eq [IntPtr]::Zero) { $progmanWpe = [MineradioWallpaperWin]::FindWindowEx($progman, [IntPtr]::Zero, "WPEWebWallpaper", $null) }
if ($progmanWpe -ne [IntPtr]::Zero) {
  $desktopParent = $progman
  $iconHost = $progmanDefView
}
$progmanChildWorker = [MineradioWallpaperWin]::FindWindowEx($progman, [IntPtr]::Zero, "WorkerW", $null)
while ($progmanChildWorker -ne [IntPtr]::Zero -and $desktopParent -eq [IntPtr]::Zero) {
  $childDefView = [MineradioWallpaperWin]::FindWindowEx($progmanChildWorker, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
  if ($childDefView -eq [IntPtr]::Zero -and [MineradioWallpaperWin]::IsWindowVisible($progmanChildWorker)) {
    $desktopParent = $progmanChildWorker
    $iconHost = $progmanDefView
    break
  }
  $progmanChildWorker = [MineradioWallpaperWin]::FindWindowEx($progman, $progmanChildWorker, "WorkerW", $null)
}
if ($desktopParent -eq [IntPtr]::Zero) { $desktopParent = $progman }
$GWL_STYLE = -16
$WS_BORDER = 0x00800000L
$WS_DLGFRAME = 0x00400000L
$WS_THICKFRAME = 0x00040000L
$WS_CAPTION = 0x00C00000L
$WS_POPUP = 0x80000000L
$WS_CHILD = 0x40000000L
$WS_VISIBLE = 0x10000000L
$style = [MineradioWallpaperWin]::GetWindowLongPtr($target, $GWL_STYLE).ToInt64()
$style = $style -band (-bnot ($WS_BORDER -bor $WS_DLGFRAME -bor $WS_THICKFRAME -bor $WS_CAPTION -bor $WS_POPUP))
$style = $style -bor $WS_CHILD -bor $WS_VISIBLE
[MineradioWallpaperWin]::SetWindowLongPtr($target, $GWL_STYLE, [IntPtr]$style) | Out-Null
[MineradioWallpaperWin]::SetParent($target, $desktopParent) | Out-Null
$rect = New-Object MineradioWallpaperWin+RECT
[MineradioWallpaperWin]::GetClientRect($desktopParent, [ref]$rect) | Out-Null
$width = [Math]::Max(1, $rect.Right - $rect.Left)
$height = [Math]::Max(1, $rect.Bottom - $rect.Top)
if ($width -le 1 -or $height -le 1) {
  $width = [MineradioWallpaperWin]::GetSystemMetrics(78)
  $height = [MineradioWallpaperWin]::GetSystemMetrics(79)
}
$SWP_NOACTIVATE = 0x0010
$SWP_FRAMECHANGED = 0x0020
$SWP_SHOWWINDOW = 0x0040
$SWP_NOSIZE = 0x0001
$SWP_NOMOVE = 0x0002
$SWP_NOOWNERZORDER = 0x0200
$insertAfter = [IntPtr]::Zero
$parentDefView = [MineradioWallpaperWin]::FindWindowEx($desktopParent, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
if ($parentDefView -ne [IntPtr]::Zero) { $insertAfter = $parentDefView }
[MineradioWallpaperWin]::SetWindowPos($target, $insertAfter, 0, 0, $width, $height, $SWP_NOACTIVATE -bor $SWP_FRAMECHANGED -bor $SWP_SHOWWINDOW) | Out-Null
if ($targetWpe -ne [IntPtr]::Zero -and [MineradioWallpaperWin]::GetParent($targetWpe) -eq $desktopParent) {
  [MineradioWallpaperWin]::SetWindowPos($targetWpe, $target, 0, 0, 0, 0, $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE -bor $SWP_NOOWNERZORDER) | Out-Null
  [MineradioWallpaperWin]::SetWindowPos($target, [IntPtr]::Zero, 0, 0, $width, $height, $SWP_NOACTIVATE -bor $SWP_FRAMECHANGED -bor $SWP_SHOWWINDOW) | Out-Null
}
Write-Output ("parent=0x{0:X};target=0x{1:X};wpe=0x{2:X};size={3}x{4}" -f $desktopParent.ToInt64(), $target.ToInt64(), $targetWpe.ToInt64(), $width, $height)
`;
  execFile('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    windowsHide: true,
    timeout: 5000,
  }, (error, stdout, stderr) => {
    wallpaperAttachInFlight = false;
    const shouldRefreshAfterAttach = wallpaperAttachPending;
    wallpaperAttachPending = false;
    if (!error) wallpaperAttachedHwnd = hwnd;
    if (error) console.warn('Wallpaper strict attach failed:', error.message);
    wallpaperDebugLog('attach-done', {
      ok: !error,
      mode: 'transparent-progman-child',
      error: error ? error.message : '',
      info: stdout ? String(stdout).trim().slice(0, 300) : '',
      stderr: stderr ? String(stderr).trim().slice(0, 300) : '',
    });
    if (typeof done === 'function') done(true);
    if (shouldRefreshAfterAttach && wallpaperWindow && !wallpaperWindow.isDestroyed() && wallpaperState.enabled) {
      setTimeout(() => attachWallpaperToWorkerW(wallpaperWindow), 120);
    }
  });
}

function configureDirectWallpaperWindow(win) {
  if (process.platform !== 'win32' || !win || win.isDestroyed()) return;
  const hwnd = nativeWindowHandleDecimal(win);
  if (!hwnd) return;
  const bounds = wallpaperElectronBounds();
  const script = `
$ErrorActionPreference = "Stop"
if (-not ("MineradioDirectWallpaperWin" -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MineradioDirectWallpaperWin {
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
  [DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtr", SetLastError=true)] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
  [DllImport("user32.dll", EntryPoint="SetWindowLongPtr", SetLastError=true)] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
"@
}
$target = [IntPtr]([Int64]${hwnd})
$GWL_STYLE = -16
$WS_BORDER = 0x00800000L
$WS_DLGFRAME = 0x00400000L
$WS_THICKFRAME = 0x00040000L
$WS_CAPTION = 0x00C00000L
$WS_POPUP = 0x80000000L
$WS_CHILD = 0x40000000L
$WS_VISIBLE = 0x10000000L
$style = [MineradioDirectWallpaperWin]::GetWindowLongPtr($target, $GWL_STYLE).ToInt64()
$style = $style -band (-bnot ($WS_BORDER -bor $WS_DLGFRAME -bor $WS_THICKFRAME -bor $WS_CAPTION -bor $WS_CHILD))
$style = $style -bor $WS_POPUP -bor $WS_VISIBLE
[MineradioDirectWallpaperWin]::SetWindowLongPtr($target, $GWL_STYLE, [IntPtr]$style) | Out-Null
[MineradioDirectWallpaperWin]::SetParent($target, [IntPtr]::Zero) | Out-Null
[MineradioDirectWallpaperWin]::SetWindowRgn($target, [IntPtr]::Zero, $true) | Out-Null
$HWND_BOTTOM = [IntPtr]1
$SWP_NOACTIVATE = 0x0010
$SWP_FRAMECHANGED = 0x0020
$SWP_SHOWWINDOW = 0x0040
[MineradioDirectWallpaperWin]::SetWindowPos($target, $HWND_BOTTOM, ${Math.trunc(bounds.x)}, ${Math.trunc(bounds.y)}, ${Math.trunc(bounds.width)}, ${Math.trunc(bounds.height)}, $SWP_NOACTIVATE -bor $SWP_FRAMECHANGED -bor $SWP_SHOWWINDOW) | Out-Null
`;
  execFile('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    windowsHide: true,
    timeout: 5000,
  }, (error) => {
    if (error) console.warn('Wallpaper direct window configure failed:', error.message);
    wallpaperDebugLog('direct-window-configure', {
      ok: !error,
      hwnd,
      bounds,
      error: error ? error.message : '',
    });
  });
}

function wallpaperElectronBounds() {
  const displays = screen.getAllDisplays();
  if (!displays.length) return screen.getPrimaryDisplay().bounds;
  return displays.reduce((acc, display) => {
    const bounds = display.bounds;
    const left = Math.min(acc.x, bounds.x);
    const top = Math.min(acc.y, bounds.y);
    const right = Math.max(acc.x + acc.width, bounds.x + bounds.width);
    const bottom = Math.max(acc.y + acc.height, bounds.y + bounds.height);
    return { x: left, y: top, width: right - left, height: bottom - top };
  }, { ...displays[0].bounds });
}

function positionWallpaperWindow() {
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  const bounds = wallpaperElectronBounds();
  wallpaperWindow.setBounds(bounds, false);
}

function stopWallpaperFrameCapture() {
  if (wallpaperFrameCaptureTimer) {
    clearInterval(wallpaperFrameCaptureTimer);
    wallpaperFrameCaptureTimer = null;
  }
  wallpaperFrameCaptureInFlight = false;
}

function cleanupWallpaperStageFrameFiles(currentFramePath = '') {
  const now = Date.now();
  if (now - wallpaperFrameCleanupLastAt < 2200) return;
  wallpaperFrameCleanupLastAt = now;
  try {
    const dir = app.getPath('userData');
    const files = fs.readdirSync(dir)
      .filter((name) => /^wallpaper-stage-frame(?:\.mrf)?\.\d+\.mrf(?:\.\d+)?(?:\.tmp)?$/.test(name))
      .map((name) => {
        const full = path.join(dir, name);
        let mtime = 0;
        try { mtime = fs.statSync(full).mtimeMs; } catch (_) {}
        return { name, full, mtime };
      })
      .sort((a, b) => b.mtime - a.mtime);
    files.slice(5).forEach((file) => {
      if (file.full === currentFramePath) return;
      try { fs.unlinkSync(file.full); } catch (_) {}
    });
  } catch (_) {}
}

async function captureWallpaperStageFrame() {
  if (!USE_NATIVE_WALLPAPER_FRAME_PRESENTER) return;
  if (!wallpaperState.enabled) return;
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  if (wallpaperFrameCaptureInFlight) return;
  wallpaperFrameCaptureInFlight = true;
  let tmp = '';
  try {
    const image = await wallpaperWindow.webContents.capturePage();
    if (!image || image.isEmpty()) return;
    const frameBasePath = wallpaperStageFramePath();
    const framePrefix = frameBasePath.replace(/\.mrf$/i, '');
    const framePath = `${framePrefix}.${Date.now()}.mrf`;
    tmp = `${framePath}.${process.pid}.tmp`;
    const size = image.getSize();
    const bitmap = image.toBitmap();
    const stride = size.width * 4;
    if (!size.width || !size.height || !bitmap || bitmap.length < stride * size.height) return;
    const header = Buffer.alloc(16);
    header.write('MRF1', 0, 4, 'ascii');
    header.writeUInt32LE(size.width, 4);
    header.writeUInt32LE(size.height, 8);
    header.writeUInt32LE(stride, 12);
    fs.mkdirSync(path.dirname(framePath), { recursive: true });
    fs.writeFileSync(tmp, Buffer.concat([header, bitmap]));
    fs.renameSync(tmp, framePath);
    cleanupWallpaperStageFrameFiles(framePath);
    if (!wallpaperState.framePath) {
      wallpaperState = { ...wallpaperState, framePath: frameBasePath, frameSource: 'stage-capture', frameFormat: 'raw-bgra' };
      writeNativeWallpaperState();
    }
  } catch (e) {
    if (tmp) {
      try { fs.unlinkSync(tmp); } catch (_) {}
    }
    const now = Date.now();
    if (now - wallpaperFrameCaptureLastLogAt > 2500) {
      wallpaperFrameCaptureLastLogAt = now;
      wallpaperDebugLog('stage-frame-capture-failed', { error: e.message || String(e) });
    }
  } finally {
    wallpaperFrameCaptureInFlight = false;
  }
}

function startWallpaperFrameCapture() {
  if (!USE_NATIVE_WALLPAPER_FRAME_PRESENTER) return;
  if (!wallpaperState.enabled) return;
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  if (!wallpaperFrameCaptureTimer) {
    wallpaperFrameCaptureTimer = setInterval(captureWallpaperStageFrame, 66);
    wallpaperDebugLog('stage-frame-capture-start', { framePath: wallpaperStageFramePath() });
  }
  setTimeout(captureWallpaperStageFrame, 80);
  setTimeout(captureWallpaperStageFrame, 260);
  setTimeout(captureWallpaperStageFrame, 620);
}

function sendWallpaperState(options = {}) {
  const isNativeWallpaper = !!(wallpaperWindow && wallpaperWindow.nativeWallpaper);
  let sentNativePipe = false;
  if (isNativeWallpaper) sentNativePipe = sendNativeWallpaperPipeState();
  if (isNativeWallpaper && !sentNativePipe) {
    scheduleNativeWallpaperPipeConnect(30);
  }
  if (options.persist !== false) {
    nativeWallpaperFallbackWriteLastAt = Date.now();
    writeNativeWallpaperState();
  } else if (isNativeWallpaper && !sentNativePipe) {
    const now = Date.now();
    const fallbackInterval = wallpaperState && wallpaperState.enabled && wallpaperState.playing ? 90 : 350;
    if (now - nativeWallpaperFallbackWriteLastAt > fallbackInterval) {
      nativeWallpaperFallbackWriteLastAt = now;
      writeNativeWallpaperState();
    }
  }
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  try { wallpaperWindow.webContents.send('mineradio-wallpaper-state', wallpaperState); } catch (_) {}
}

function sendWallpaperStateSoon(delay = 80, options = {}) {
  setTimeout(() => {
    if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
    sendWallpaperState(options);
  }, delay);
}

function showAndAttachWallpaperWindow() {
  if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
  if (wallpaperWindow.nativeWallpaper) {
    markWallpaperWindowLoaded('native-d3d11');
    sendWallpaperState({ persist: false });
    return;
  }
  const useDirectElectronWallpaper = USE_DIRECT_ELECTRON_WALLPAPER && !shouldUseWebglSonicWallpaper(wallpaperState);
  if (wallpaperSuspendedForMainWindow && useDirectElectronWallpaper) {
    try { if (wallpaperWindow.isVisible()) wallpaperWindow.hide(); } catch (_) {}
    return;
  }
  positionWallpaperWindow();
  if (useDirectElectronWallpaper) {
    try {
      if (!wallpaperWindow.isVisible()) wallpaperWindow.showInactive();
      startWallpaperClipController(wallpaperWindow);
      if (USE_DIRECT_WALLPAPER_WINDOW_CONFIGURE) configureDirectWallpaperWindow(wallpaperWindow);
      wallpaperDebugLog('show-direct-electron', {
        visible: wallpaperWindow.isVisible(),
        bounds: wallpaperWindow.getBounds(),
        enabled: !!wallpaperState.enabled,
      });
      if (wallpaperWindow.isVisible()) markWallpaperWindowLoaded('show-direct-electron');
    } catch (e) {
      wallpaperDebugLog('show-direct-electron-failed', { error: e.message || String(e) });
    }
    return;
  }
  wallpaperDebugLog('show-attach', {
    visible: wallpaperWindow.isVisible(),
    bounds: wallpaperWindow.getBounds(),
    enabled: !!wallpaperState.enabled,
  });
  if (wallpaperAttachRetryTimer) clearTimeout(wallpaperAttachRetryTimer);
  attachWallpaperToWorkerW(wallpaperWindow, (attached) => {
    if (!attached) return;
    if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
    try {
      if (!wallpaperWindow.isVisible()) wallpaperWindow.showInactive();
      wallpaperDebugLog('show-inactive', {
        visible: wallpaperWindow.isVisible(),
        bounds: wallpaperWindow.getBounds(),
      });
      if (wallpaperWindow.isVisible()) markWallpaperWindowLoaded('show-attach');
    } catch (e) {
      console.warn('Wallpaper show failed:', e.message);
      wallpaperDebugLog('show-failed', { error: e.message });
    }
  });
  wallpaperAttachRetryTimer = setTimeout(() => {
    wallpaperAttachRetryTimer = null;
    if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
    positionWallpaperWindow();
    attachWallpaperToWorkerW(wallpaperWindow);
  }, 900);
}

function markWallpaperWindowLoaded(reason) {
  wallpaperWindowLoadReady = true;
  wallpaperDebugLog('load-ready', { reason });
  if (wallpaperWindowLoadResolve) {
    const resolve = wallpaperWindowLoadResolve;
    wallpaperWindowLoadResolve = null;
    wallpaperWindowLoadReject = null;
    if (wallpaperWindowLoadTimer) {
      clearTimeout(wallpaperWindowLoadTimer);
      wallpaperWindowLoadTimer = null;
    }
    wallpaperWindowLoadPromise = null;
    resolve(wallpaperWindow);
  }
}

function waitForWallpaperWindowLoaded(win, timeoutMs = 8000) {
  if (!win || win.isDestroyed()) return Promise.reject(new Error('WALLPAPER_WINDOW_MISSING'));
  if (win.nativeWallpaper) return Promise.resolve(win);
  if (wallpaperWindowLoadReady) return Promise.resolve(win);
  if (wallpaperWindowLoadPromise) return wallpaperWindowLoadPromise;
  wallpaperWindowLoadPromise = new Promise((resolve, reject) => {
    wallpaperWindowLoadResolve = resolve;
    wallpaperWindowLoadReject = reject;
    let settled = false;
    const cleanup = () => {
      if (wallpaperWindowLoadTimer) {
        clearTimeout(wallpaperWindowLoadTimer);
        wallpaperWindowLoadTimer = null;
      }
      try {
        if (win && !win.isDestroyed()) {
          win.webContents.removeListener('did-finish-load', onLoaded);
          win.webContents.removeListener('did-fail-load', onFailed);
          win.webContents.removeListener('render-process-gone', onGone);
          win.removeListener('closed', onClosed);
        }
      } catch (_) {}
    };
    const finish = () => {
      if (settled) return;
      settled = true;
      cleanup();
      wallpaperWindowLoadReady = true;
      wallpaperWindowLoadPromise = null;
      wallpaperWindowLoadResolve = null;
      wallpaperWindowLoadReject = null;
      resolve(win);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      wallpaperWindowLoadPromise = null;
      wallpaperWindowLoadResolve = null;
      wallpaperWindowLoadReject = null;
      reject(error);
    };
    const onLoaded = () => finish();
    const onFailed = (_event, errorCode, errorDescription) => {
      fail(new Error(`WALLPAPER_LOAD_FAILED:${errorCode}:${errorDescription || ''}`));
    };
    const onGone = (_event, details) => {
      fail(new Error(`WALLPAPER_RENDERER_GONE:${details && details.reason || 'unknown'}`));
    };
    const onClosed = () => fail(new Error('WALLPAPER_WINDOW_CLOSED'));
    wallpaperWindowLoadTimer = setTimeout(() => fail(new Error('WALLPAPER_LOAD_TIMEOUT')), timeoutMs);
    win.webContents.once('did-finish-load', onLoaded);
    win.webContents.once('did-fail-load', onFailed);
    win.webContents.once('render-process-gone', onGone);
    win.once('closed', onClosed);
  });
  return wallpaperWindowLoadPromise;
}

function createWallpaperWindow(payload = {}) {
  wallpaperState = { ...mergeWallpaperPayloadPreservingBackground(wallpaperState, payload), enabled: true };
  const wantsNativeWallpaper = process.platform === 'win32'
    && (USE_NATIVE_WALLPAPER || shouldUseNativeD3D11Wallpaper(wallpaperState));
  if (wantsNativeWallpaper && wallpaperWindow && !wallpaperWindow.isDestroyed() && !wallpaperWindow.nativeWallpaper) {
    wallpaperDebugLog('webgl-stop-for-native-preset', { preset: wallpaperPresetForRenderer(wallpaperState) });
    try { wallpaperWindow.close(); } catch (_) {}
    wallpaperWindow = null;
    wallpaperWindowLoadReady = false;
    wallpaperWindowLoadPromise = null;
  }
  if (!wantsNativeWallpaper && nativeWallpaperProcess && !nativeWallpaperProcess.killed) {
    wallpaperDebugLog('native-stop-for-webgl-preset', { preset: wallpaperPresetForRenderer(wallpaperState) });
    try { nativeWallpaperProcess.kill(); } catch (_) {}
    nativeWallpaperProcess = null;
    closeNativeWallpaperPipe();
    nativeWallpaperWindowShim = null;
    if (wallpaperWindow && wallpaperWindow.nativeWallpaper) wallpaperWindow = null;
  }
  const nativeWin = wantsNativeWallpaper
    ? createNativeWallpaperWindow(payload)
    : null;
  if (nativeWin) {
    wallpaperWindow = nativeWin;
    startWallpaperEngineConfigWatcher();
    sendWallpaperState();
    return nativeWin;
  }
  if (process.platform === 'win32' && USE_NATIVE_D3D11_WALLPAPER && shouldUseNativeD3D11Wallpaper(wallpaperState)) {
    throw new Error('NATIVE_D3D11_WALLPAPER_MISSING');
  }
  wallpaperDebugLog('create-request', {
    hasWindow: !!(wallpaperWindow && !wallpaperWindow.isDestroyed()),
    payloadEnabled: payload && payload.enabled,
    stateEnabled: wallpaperState.enabled,
  });
  if (wallpaperWindow && !wallpaperWindow.isDestroyed()) {
    if (!wallpaperWindow.isVisible() || USE_DIRECT_ELECTRON_WALLPAPER) showAndAttachWallpaperWindow();
    positionWallpaperWindow();
    sendWallpaperState();
    startWallpaperMousePoller();
    startWallpaperEngineConfigWatcher();
    wallpaperDebugLog('create-reuse', {
      visible: wallpaperWindow.isVisible(),
      bounds: wallpaperWindow.getBounds(),
    });
    return wallpaperWindow;
  }
  wallpaperWindowLoadReady = false;
  wallpaperWindowLoadPromise = null;
  const bounds = wallpaperElectronBounds();
  wallpaperWindow = new BrowserWindow({
    ...bounds,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    resizable: false,
    movable: false,
    focusable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    show: false,
    paintWhenInitiallyHidden: true,
    title: 'Mineradio Wallpaper',
    webPreferences: {
      preload: path.join(__dirname, 'overlay-preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      webSecurity: false,
      backgroundThrottling: false,
    },
  });
  wallpaperWindow.setIgnoreMouseEvents(true);
  wallpaperWindow.on('minimize', () => {
    if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
    wallpaperDebugLog('minimize-restore');
    setTimeout(() => {
      if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
      try { wallpaperWindow.restore(); } catch (_) {}
      showAndAttachWallpaperWindow();
    }, 40);
  });
  wallpaperWindow.on('hide', () => {
    if (wallpaperSuspendedForMainWindow) return;
    if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
    wallpaperDebugLog('hide-restore');
    setTimeout(() => {
      if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
      showAndAttachWallpaperWindow();
    }, 40);
  });
  wallpaperWindow.webContents.on('console-message', (_event, level, message) => {
    if (level < 2) return;
    const text = String(message || '');
    const key = text.includes('WebGL: INVALID_OPERATION') || text.includes('WebGL: too many errors')
      ? 'webgl-invalid-program'
      : text.slice(0, 180);
    const now = Date.now();
    const lastAt = wallpaperRendererConsoleLast.get(key) || 0;
    if (now - lastAt < 5000) return;
    wallpaperRendererConsoleLast.set(key, now);
    console.warn('Wallpaper renderer:', text);
    wallpaperDebugLog('renderer-console', { level, message: text });
  });
  wallpaperWindow.webContents.on('render-process-gone', (_event, details) => {
    console.warn('Wallpaper renderer gone:', details && details.reason);
    wallpaperDebugLog('renderer-gone', details || {});
  });
  wallpaperWindow.webContents.on('did-fail-load', (_event, errorCode, errorDescription) => {
    console.warn('Wallpaper load failed:', errorCode, errorDescription);
    wallpaperDebugLog('load-failed', { errorCode, errorDescription });
  });
  wallpaperWindow.once('ready-to-show', () => {
    if (!wallpaperWindow || wallpaperWindow.isDestroyed()) return;
    wallpaperDebugLog('ready-to-show', { bounds: wallpaperWindow.getBounds() });
    markWallpaperWindowLoaded('ready-to-show');
    if (USE_DIRECT_ELECTRON_WALLPAPER) {
      showAndAttachWallpaperWindow();
    } else {
      showAndAttachWallpaperWindow();
    }
    sendWallpaperState();
    sendWallpaperStateSoon(180);
    startWallpaperMousePoller();
  });
  wallpaperWindow.webContents.once('did-finish-load', () => {
    wallpaperDebugLog('did-finish-load', { enabled: !!wallpaperState.enabled });
    markWallpaperWindowLoaded('did-finish-load');
    if (wallpaperState.enabled) {
      if (USE_DIRECT_ELECTRON_WALLPAPER) {
        showAndAttachWallpaperWindow();
      } else {
        showAndAttachWallpaperWindow();
      }
    }
    sendWallpaperState();
    sendWallpaperStateSoon(120);
    sendWallpaperStateSoon(420);
    startWallpaperMousePoller();
  });
  const createdWallpaperWindow = wallpaperWindow;
  wallpaperWindow.on('closed', () => {
    wallpaperDebugLog('closed');
    stopWallpaperMousePoller();
    stopWallpaperEngineConfigWatchers();
    if (wallpaperWindow === createdWallpaperWindow) wallpaperWindow = null;
    wallpaperWindowLoadReady = false;
    wallpaperWindowLoadPromise = null;
    wallpaperAttachInFlight = false;
    wallpaperAttachPending = false;
    wallpaperAttachedHwnd = '';
    wallpaperSuspendedForMainWindow = false;
    broadcastWallpaperEnabledState(false);
  });
  const stagePage = shouldUseWebglSonicWallpaper(wallpaperState)
    ? 'wallpaper-sonic.html?wallpaperStage=1'
    : 'index.html?wallpaperStage=1';
  const url = overlayUrl(stagePage);
  wallpaperDebugLog('load-url', { url, bounds });
  wallpaperWindow.loadURL(url).catch((e) => {
    console.warn('Wallpaper load failed:', e.message);
    wallpaperDebugLog('load-url-failed', { error: e.message });
  });
  setTimeout(() => {
    if (!wallpaperWindow || wallpaperWindow.isDestroyed() || !wallpaperState.enabled) return;
    showAndAttachWallpaperWindow();
    sendWallpaperState();
  }, 80);
  startWallpaperEngineConfigWatcher();
  return wallpaperWindow;
}

function closeWallpaperWindow(options = {}) {
  const persistDisabled = options.persistDisabled === true;
  if (persistDisabled) {
    wallpaperState = { ...wallpaperState, enabled: false };
    writeNativeWallpaperState();
  }
  wallpaperDebugLog('close-request', {
    hasWindow: !!(wallpaperWindow && !wallpaperWindow.isDestroyed()),
    persistDisabled,
  });
  stopWallpaperMousePoller();
  stopWallpaperClipController();
  stopWallpaperEngineConfigWatchers();
  if (wallpaperAttachRetryTimer) {
    clearTimeout(wallpaperAttachRetryTimer);
    wallpaperAttachRetryTimer = null;
  }
  wallpaperAttachInFlight = false;
  wallpaperAttachPending = false;
  wallpaperAttachedHwnd = '';
  wallpaperSuspendedForMainWindow = false;
  if (wallpaperWindow && !wallpaperWindow.isDestroyed()) {
    if (persistDisabled) sendWallpaperState();
    wallpaperWindow.close();
  }
  if (nativeWallpaperProcess && !nativeWallpaperProcess.killed) {
    nativeWallpaperProcess.kill();
    nativeWallpaperProcess = null;
  }
  closeNativeWallpaperPipe();
  nativeWallpaperWindowShim = null;
  wallpaperWindow = null;
}

function closeOverlayWindows() {
  closeDesktopLyricsWindow();
  closeWallpaperWindow();
}

ipcMain.handle('desktop-window-minimize', (event) => {
  getSenderWindow(event)?.minimize();
});

ipcMain.handle('desktop-window-toggle-maximize', (event) => {
  toggleNativeMaximize(getSenderWindow(event));
  return getWindowState(getSenderWindow(event));
});

ipcMain.handle('desktop-window-toggle-fullscreen', (event) => {
  toggleFullscreen(getSenderWindow(event));
});

ipcMain.handle('desktop-window-exit-fullscreen-windowed', (event) => {
  exitFullscreenToWindow(getSenderWindow(event));
});

ipcMain.handle('desktop-window-get-state', (event) => {
  return getWindowState(getSenderWindow(event));
});

function resizeBoundsFromSession(session, point) {
  const dx = point.x - session.startPoint.x;
  const dy = point.y - session.startPoint.y;
  const dir = session.direction;
  const start = session.startBounds;
  let x = start.x;
  let y = start.y;
  let width = start.width;
  let height = start.height;
  if (dir.includes('e')) width = start.width + dx;
  if (dir.includes('s')) height = start.height + dy;
  if (dir.includes('w')) {
    x = start.x + dx;
    width = start.width - dx;
    if (width < MIN_WINDOWED_WIDTH) {
      x = start.x + start.width - MIN_WINDOWED_WIDTH;
      width = MIN_WINDOWED_WIDTH;
    }
  }
  if (dir.includes('n')) {
    y = start.y + dy;
    height = start.height - dy;
    if (height < MIN_WINDOWED_HEIGHT) {
      y = start.y + start.height - MIN_WINDOWED_HEIGHT;
      height = MIN_WINDOWED_HEIGHT;
    }
  }
  width = Math.max(MIN_WINDOWED_WIDTH, width);
  height = Math.max(MIN_WINDOWED_HEIGHT, height);
  return {
    x: Math.round(x),
    y: Math.round(y),
    width: Math.round(width),
    height: Math.round(height),
  };
}

ipcMain.on('desktop-window-resize-start-fast', (event, payload = {}) => {
  const win = getSenderWindow(event);
  const direction = String(payload.direction || '').toLowerCase();
  if (!win || win.isDestroyed() || win.isFullScreen() || win.isMaximized() || !/^[nsew]{1,2}$/.test(direction)) {
    mainWindowResizeSession = null;
    return;
  }
  mainWindowSnapRestoreBounds = null;
  mainWindowResizeSession = {
    win,
    direction,
    startPoint: getWindowDragPoint(payload),
    startBounds: win.getBounds(),
  };
});

ipcMain.on('desktop-window-resize-move-fast', (event, payload = {}) => {
  const win = getSenderWindow(event);
  const session = mainWindowResizeSession;
  if (!win || win.isDestroyed() || !session || session.win !== win || win.isFullScreen() || win.isMaximized()) return;
  win.setBounds(resizeBoundsFromSession(session, getWindowDragPoint(payload)), false);
});

ipcMain.on('desktop-window-resize-end-fast', (event) => {
  const win = getSenderWindow(event);
  if (!win || !mainWindowResizeSession || mainWindowResizeSession.win === win) {
    mainWindowResizeSession = null;
  }
  if (win && !win.isDestroyed()) {
    sendWindowState(win);
    scheduleDesktopWindowStateSave(win, 80);
  }
});

function getWindowDragPoint(payload) {
  const x = Number(payload && payload.screenX);
  const y = Number(payload && payload.screenY);
  if (Number.isFinite(x) && Number.isFinite(y)) {
    return { x: Math.round(x), y: Math.round(y) };
  }
  return screen.getCursorScreenPoint();
}

function clampNumber(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function boundsCloseTo(a, b, tolerance = 4) {
  if (!a || !b) return false;
  return Math.abs(a.x - b.x) <= tolerance
    && Math.abs(a.y - b.y) <= tolerance
    && Math.abs(a.width - b.width) <= tolerance
    && Math.abs(a.height - b.height) <= tolerance;
}

function snapBoundsForPoint(point) {
  const display = screen.getDisplayNearestPoint(point);
  const area = display.workArea;
  const nearLeft = point.x <= area.x + WINDOW_SNAP_EDGE;
  const nearRight = point.x >= area.x + area.width - WINDOW_SNAP_EDGE;
  const nearTop = point.y <= area.y + WINDOW_SNAP_EDGE;
  const nearBottom = point.y >= area.y + area.height - WINDOW_SNAP_EDGE;
  if (nearTop && !nearLeft && !nearRight) return { type: 'maximize' };
  const halfWidth = Math.round(area.width / 2);
  const halfHeight = Math.round(area.height / 2);
  if (nearLeft && nearTop) return { type: 'bounds', bounds: { x: area.x, y: area.y, width: halfWidth, height: halfHeight } };
  if (nearRight && nearTop) return { type: 'bounds', bounds: { x: area.x + area.width - halfWidth, y: area.y, width: halfWidth, height: halfHeight } };
  if (nearLeft && nearBottom) return { type: 'bounds', bounds: { x: area.x, y: area.y + area.height - halfHeight, width: halfWidth, height: halfHeight } };
  if (nearRight && nearBottom) return { type: 'bounds', bounds: { x: area.x + area.width - halfWidth, y: area.y + area.height - halfHeight, width: halfWidth, height: halfHeight } };
  if (nearLeft) return { type: 'bounds', bounds: { x: area.x, y: area.y, width: halfWidth, height: area.height } };
  if (nearRight) return { type: 'bounds', bounds: { x: area.x + area.width - halfWidth, y: area.y, width: halfWidth, height: area.height } };
  return null;
}

function normalBoundsForDragRestore(win, point) {
  const saved = readDesktopWindowState();
  const fallback = getWindowedBounds(win);
  const base = mainWindowSnapRestoreBounds || saved.normalBounds || saved.bounds || fallback;
  const display = screen.getDisplayNearestPoint(point);
  const area = display.workArea;
  const width = Math.min(Math.max(MIN_WINDOWED_WIDTH, base.width), area.width);
  const height = Math.min(Math.max(MIN_WINDOWED_HEIGHT, base.height), area.height);
  return { x: base.x, y: base.y, width, height };
}

function beginMainWindowDrag(win, payload = {}) {
  if (!win || win.isDestroyed() || win.isFullScreen()) {
    mainWindowDragSession = null;
    return { ok: false, reason: 'not-draggable' };
  }
  const point = getWindowDragPoint(payload);
  let startBounds = win.getBounds();
  if (win.isMaximized() || (mainWindowSnapRestoreBounds && boundsCloseTo(startBounds, mainWindowSnapRestoreBounds) === false)) {
    const current = win.getBounds();
    const normal = normalBoundsForDragRestore(win, point);
    const ratioX = clampNumber((point.x - current.x) / Math.max(1, current.width), 0.12, 0.88);
    startBounds = {
      x: Math.round(point.x - normal.width * ratioX),
      y: Math.round(point.y - 20),
      width: normal.width,
      height: normal.height,
    };
    if (win.isMaximized()) win.unmaximize();
    win.setBounds(startBounds, false);
  }
  mainWindowDragSession = {
    win,
    startPoint: point,
    lastPoint: point,
    startBounds,
    restoreBounds: mainWindowSnapRestoreBounds || startBounds,
  };
  return { ok: true };
}

function moveMainWindowDrag(win, payload = {}) {
  const session = mainWindowDragSession;
  if (!win || win.isDestroyed() || !session || session.win !== win || win.isFullScreen()) return { ok: false };
  const point = getWindowDragPoint(payload);
  session.lastPoint = point;
  win.setBounds({
    x: Math.round(session.startBounds.x + point.x - session.startPoint.x),
    y: Math.round(session.startBounds.y + point.y - session.startPoint.y),
    width: session.startBounds.width,
    height: session.startBounds.height,
  }, false);
  return { ok: true };
}

function finishMainWindowDrag(win) {
  const session = mainWindowDragSession;
  if (!win || win.isDestroyed() || !session || session.win !== win) {
    mainWindowDragSession = null;
    return { ok: false };
  }
  const point = session.lastPoint || screen.getCursorScreenPoint();
  const snap = snapBoundsForPoint(point);
  if (snap && !win.isFullScreen()) {
    if (snap.type === 'maximize') {
      mainWindowSnapRestoreBounds = session.restoreBounds || win.getBounds();
      writeDesktopWindowStateFor(win);
      win.maximize();
    } else if (snap.bounds) {
      mainWindowSnapRestoreBounds = session.restoreBounds || win.getBounds();
      if (win.isMaximized()) win.unmaximize();
      win.setBounds(snap.bounds, true);
    }
  } else {
    mainWindowSnapRestoreBounds = null;
  }
  mainWindowDragSession = null;
  sendWindowState(win);
  scheduleDesktopWindowStateSave(win, 80);
  return { ok: true };
}

ipcMain.on('desktop-window-drag-start-fast', (event, payload = {}) => {
  const win = getSenderWindow(event);
  beginMainWindowDrag(win, payload);
});

ipcMain.on('desktop-window-drag-move-fast', (event, payload = {}) => {
  const win = getSenderWindow(event);
  moveMainWindowDrag(win, payload);
});

ipcMain.on('desktop-window-drag-end-fast', (event) => {
  const win = getSenderWindow(event);
  finishMainWindowDrag(win);
});

ipcMain.handle('desktop-window-drag-start', (event) => {
  const win = getSenderWindow(event);
  return beginMainWindowDrag(win, {});
});

ipcMain.handle('desktop-window-drag-move', (event) => {
  const win = getSenderWindow(event);
  return moveMainWindowDrag(win, {});
});

ipcMain.handle('desktop-window-drag-end', (event) => {
  const win = getSenderWindow(event);
  return finishMainWindowDrag(win);
});

ipcMain.handle('desktop-window-close', (event) => {
  const win = getSenderWindow(event);
  if (readDesktopSettings().closeToTray) {
    hideMainWindowToBackground(win);
  } else if (win && !win.isDestroyed()) {
    pausePlaybackBeforeQuit('window-close');
    isQuitting = true;
    win.close();
  }
});

ipcMain.handle('mineradio-desktop-settings-get', () => readDesktopSettings());

ipcMain.handle('mineradio-desktop-settings-set', (_event, patch) => {
  try {
    const settings = writeDesktopSettings(patch || {});
    updateTrayMenu();
    return { ok: true, settings };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_SETTINGS_FAILED', settings: readDesktopSettings() };
  }
});

ipcMain.handle('mineradio-tray-player-state', (_event, state = {}) => {
  const volume = Number(state.volume);
  trayPlayerState = {
    ...trayPlayerState,
    title: String(state.title || '').slice(0, 120),
    artist: String(state.artist || '').slice(0, 120),
    cover: String(state.cover || '').slice(0, 2048),
    playing: !!state.playing,
    liked: !!state.liked,
    desktopLyrics: !!state.desktopLyrics,
    wallpaperMode: !!state.wallpaperMode,
    volume: Number.isFinite(volume) ? Math.max(0, Math.min(1, volume)) : trayPlayerState.volume,
    thumbnailClip: normalizeTaskbarThumbnailClip(state.thumbnailClip) || trayPlayerState.thumbnailClip,
  };
  updateTrayMenu();
  return { ok: true };
});

ipcMain.handle('mineradio-tray-menu-get-state', () => trayMenuState());

ipcMain.handle('mineradio-tray-menu-close', () => {
  hideTrayMenuWindow();
  return { ok: true };
});

ipcMain.handle('mineradio-tray-menu-resize-to-content', (_event, height) => {
  const nextHeight = Math.round(Math.max(TRAY_MENU_MIN_HEIGHT, Math.min(TRAY_MENU_MAX_HEIGHT, Number(height) || TRAY_MENU_DEFAULT_HEIGHT)));
  if (Math.abs(nextHeight - trayMenuHeight) < 2) return { ok: true, height: trayMenuHeight };
  trayMenuHeight = nextHeight;
  if (trayMenuWindow && !trayMenuWindow.isDestroyed()) positionTrayMenuWindow();
  return { ok: true, height: trayMenuHeight };
});

ipcMain.handle('mineradio-tray-menu-action', (_event, action) => {
  const payload = typeof action === 'object' && action
    ? { ...action }
    : { action: String(action || '').trim() };
  const actionKey = String(payload.action || '').trim();
  if (!actionKey) return { ok: false, error: 'NO_ACTION' };
  if (actionKey === 'open') {
    showMainWindowFromTray();
    hideTrayMenuWindow();
  } else if (actionKey === 'hide') {
    hideMainWindowToBackground(mainWindow);
    hideTrayMenuWindow();
  } else if (actionKey === 'exit') {
    hideTrayMenuWindow();
    quitAppFromTray();
  } else if (actionKey === 'openSettings') {
    showMainWindowFromTray();
    sendTrayPlayerAction('openSettings');
    hideTrayMenuWindow();
  } else if (actionKey === 'setVolume') {
    const volume = Number(payload.volume);
    if (!Number.isFinite(volume)) return { ok: false, error: 'BAD_VOLUME' };
    sendTrayPlayerAction({ action: 'setVolume', volume: Math.max(0, Math.min(1, volume)) });
  } else {
    sendTrayPlayerAction(actionKey);
  }
  updateTrayMenu();
  return { ok: true };
});

ipcMain.handle('mineradio-hotkeys-configure-global', (_event, bindings) => {
  return configureMineradioGlobalHotkeys(bindings);
});

ipcMain.handle('mineradio-export-json-file', async (event, payload = {}) => {
  try {
    const owner = getSenderWindow(event);
    const defaultName = String(payload.defaultName || 'mineradio-export.json').replace(/[\\/:*?"<>|]+/g, '-');
    const result = await dialog.showSaveDialog(owner, {
      title: '导出 Mineradio 存档',
      defaultPath: defaultName.toLowerCase().endsWith('.json') ? defaultName : `${defaultName}.json`,
      filters: [{ name: 'JSON', extensions: ['json'] }],
    });
    if (result.canceled || !result.filePath) return { ok: false, canceled: true };
    const text = typeof payload.text === 'string' ? payload.text : JSON.stringify(payload.data || {}, null, 2);
    fs.writeFileSync(result.filePath, text, 'utf8');
    return { ok: true, filePath: result.filePath };
  } catch (e) {
    return { ok: false, error: e.message || 'EXPORT_FAILED' };
  }
});

ipcMain.handle('mineradio-import-json-file', async (event) => {
  try {
    const owner = getSenderWindow(event);
    const result = await dialog.showOpenDialog(owner, {
      title: '导入 Mineradio 存档',
      properties: ['openFile'],
      filters: [{ name: 'JSON', extensions: ['json'] }],
    });
    if (result.canceled || !result.filePaths || !result.filePaths[0]) return { ok: false, canceled: true };
    const filePath = result.filePaths[0];
    const text = fs.readFileSync(filePath, 'utf8');
    return { ok: true, filePath, text };
  } catch (e) {
    return { ok: false, error: e.message || 'IMPORT_FAILED' };
  }
});

function localAudioFileEntry(filePath, rootPath) {
  const ext = path.extname(filePath || '').toLowerCase();
  if (!LOCAL_AUDIO_EXTS.has(ext)) return null;
  let stat = null;
  try { stat = fs.statSync(filePath); } catch (_) {}
  if (!stat || !stat.isFile()) return null;
  const name = path.basename(filePath);
  const rel = rootPath ? path.relative(rootPath, filePath) : name;
  return {
    name,
    filePath,
    url: pathToFileURL(filePath).href,
    relativePath: rel.replace(/\\/g, '/'),
    size: stat.size,
    lastModified: stat.mtimeMs,
  };
}

function scanLocalAudioFolder(folderPath, options = {}) {
  const root = path.resolve(String(folderPath || ''));
  const recursive = options && options.recursive === true;
  const files = [];
  let visited = 0;
  const maxVisited = 60000;
  const stack = [''];
  while (stack.length && visited < maxVisited) {
    const relDir = stack.pop();
    const absDir = path.join(root, relDir);
    let entries = [];
    try { entries = fs.readdirSync(absDir, { withFileTypes: true }); } catch (_) { continue; }
    entries.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }));
    for (const entry of entries) {
      visited += 1;
      if (visited > maxVisited) break;
      const rel = path.join(relDir, entry.name);
      const abs = path.join(root, rel);
      if (entry.isDirectory()) {
        if (recursive) stack.push(rel);
        continue;
      }
      if (!entry.isFile()) continue;
      const item = localAudioFileEntry(abs, root);
      if (item) files.push(item);
    }
  }
  files.sort((a, b) => String(a.relativePath || a.name).localeCompare(String(b.relativePath || b.name), undefined, { numeric: true, sensitivity: 'base' }));
  return { ok: true, folderPath: root, files, recursive, truncated: visited >= maxVisited };
}

ipcMain.handle('mineradio-local-audio-files-choose', async (event) => {
  try {
    const owner = getSenderWindow(event);
    const result = await dialog.showOpenDialog(owner, {
      title: 'Select local audio files',
      properties: ['openFile', 'multiSelections'],
      filters: [{ name: 'Audio', extensions: Array.from(LOCAL_AUDIO_EXTS).map(ext => ext.replace(/^\./, '')) }],
    });
    if (result.canceled || !result.filePaths || !result.filePaths.length) return { ok: false, canceled: true };
    const files = result.filePaths
      .map(filePath => localAudioFileEntry(filePath, path.dirname(filePath)))
      .filter(Boolean);
    return { ok: true, files };
  } catch (e) {
    return { ok: false, error: e.message || 'LOCAL_AUDIO_FILES_FAILED' };
  }
});

ipcMain.handle('mineradio-local-audio-folder-choose', async (event, options) => {
  try {
    const owner = getSenderWindow(event);
    const result = await dialog.showOpenDialog(owner, {
      title: 'Select local music folder',
      properties: ['openDirectory'],
    });
    if (result.canceled || !result.filePaths || !result.filePaths[0]) return { ok: false, canceled: true };
    return scanLocalAudioFolder(result.filePaths[0], options || {});
  } catch (e) {
    return { ok: false, error: e.message || 'LOCAL_AUDIO_FOLDER_FAILED' };
  }
});

ipcMain.handle('mineradio-local-audio-folder-scan', async (_event, folderPath, options) => {
  try {
    return scanLocalAudioFolder(folderPath, options || {});
  } catch (e) {
    return { ok: false, error: e.message || 'LOCAL_AUDIO_FOLDER_SCAN_FAILED' };
  }
});
ipcMain.handle('netease-music-open-login', async (event) => {
  return openNeteaseMusicLoginWindow(getSenderWindow(event));
});

ipcMain.handle('netease-music-clear-login', async () => {
  return clearNeteaseMusicLoginSession();
});

ipcMain.handle('qq-music-open-login', async (event, options) => {
  return openQQMusicLoginWindow(getSenderWindow(event), options || {});
});

ipcMain.handle('qq-music-clear-login', async () => {
  return clearQQMusicLoginSession();
});

ipcMain.handle('mineradio-open-update-installer', async (_event, filePath) => {
  try {
    const target = path.resolve(String(filePath || ''));
    const updateDir = path.resolve(getUpdateDownloadDir());
    if (!target || !target.startsWith(updateDir + path.sep)) {
      return { ok: false, error: 'INVALID_UPDATE_PATH' };
    }
    if (!fs.existsSync(target)) return { ok: false, error: 'UPDATE_FILE_MISSING' };
    const error = await shell.openPath(target);
    return error ? { ok: false, error } : { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'OPEN_UPDATE_FAILED' };
  }
});

ipcMain.handle('mineradio-restart-app', async () => {
  try {
    isQuitting = true;
    app.relaunch();
    app.exit(0);
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'RESTART_FAILED' };
  }
});

ipcMain.handle('mineradio-desktop-lyrics-set-enabled', async (_event, enabled, payload) => {
  try {
    clearPendingDesktopLyricsStateUpdate();
    if (enabled) {
      createDesktopLyricsWindow(payload || {});
      broadcastDesktopLyricsEnabledState(true);
    } else {
      closeDesktopLyricsWindow();
    }
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_FAILED' };
  }
});

ipcMain.on('mineradio-desktop-lyrics-update-fast', (_event, payload) => {
  try {
    queueDesktopLyricsStateUpdate(payload || {});
  } catch (e) {
    console.warn('Desktop lyrics fast update failed:', e.message || e);
  }
});

ipcMain.handle('mineradio-desktop-lyrics-set-dragging', async () => {
  return { ok: true };
});

ipcMain.handle('mineradio-desktop-lyrics-set-pointer-capture', async (_event, active) => {
  try {
    desktopLyricsPointerCapture = !!active;
    applyDesktopLyricsMouseBehavior();
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_POINTER_FAILED' };
  }
});

ipcMain.handle('mineradio-desktop-lyrics-set-hot-bounds', async (_event, bounds) => {
  try {
    const left = clampNumber(bounds && bounds.left, -2000, 4000, 0);
    const top = clampNumber(bounds && bounds.top, -2000, 4000, 0);
    const right = clampNumber(bounds && bounds.right, left + 1, 6000, left + 1);
    const bottom = clampNumber(bounds && bounds.bottom, top + 1, 6000, top + 1);
    desktopLyricsHotBounds = { left, top, right, bottom };
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_HOT_BOUNDS_FAILED' };
  }
});

ipcMain.handle('mineradio-desktop-lyrics-set-lock-state', async (_event, locked) => {
  try {
    if (desktopLyricsUserBounds) flushDesktopLyricsUserBounds();
    desktopLyricsMiddleAdjustActive = false;
    desktopLyricsState = { ...desktopLyricsState, clickThrough: !!locked, middleAdjustActive: false };
    if (desktopLyricsState.clickThrough !== false) desktopLyricsPointerCapture = false;
    applyDesktopLyricsMouseBehavior();
    broadcastDesktopLyricsLockState();
    return { ok: true, locked: desktopLyricsState.clickThrough !== false };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_LOCK_FAILED' };
  }
});

ipcMain.handle('mineradio-desktop-lyrics-move-by', async (_event, dx, dy) => {
  try {
    if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return { ok: false, error: 'NO_DESKTOP_LYRICS_WINDOW' };
    if (desktopLyricsState.clickThrough !== false) return { ok: false, error: 'DESKTOP_LYRICS_LOCKED' };
    const bounds = desktopLyricsWindow.getBounds();
    const next = {
      ...bounds,
      x: Math.round(bounds.x + clampNumber(dx, -160, 160, 0)),
      y: Math.round(bounds.y + clampNumber(dy, -160, 160, 0)),
    };
    desktopLyricsWindow.setBounds(next, false);
    desktopLyricsUserBounds = desktopLyricsWindow.getBounds();
    scheduleDesktopLyricsUserBoundsSave();
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_MOVE_FAILED' };
  }
});

ipcMain.handle('mineradio-desktop-lyrics-resize-by', async (_event, edge, dx, dy) => {
  try {
    if (!desktopLyricsWindow || desktopLyricsWindow.isDestroyed()) return { ok: false, error: 'NO_DESKTOP_LYRICS_WINDOW' };
    if (desktopLyricsState.clickThrough !== false) return { ok: false, error: 'DESKTOP_LYRICS_LOCKED' };
    edge = String(edge || '').toLowerCase();
    dx = Math.round(Number(dx) || 0);
    dy = Math.round(Number(dy) || 0);
    if (!edge || (!dx && !dy)) return { ok: true };
    const current = desktopLyricsWindow.getBounds();
    const minWidth = 360;
    const minHeight = 180;
    let next = { ...current };
    if (edge.includes('e')) next.width += dx;
    if (edge.includes('s')) next.height += dy;
    if (edge.includes('w')) {
      next.x += dx;
      next.width -= dx;
    }
    if (edge.includes('n')) {
      next.y += dy;
      next.height -= dy;
    }
    if (next.width < minWidth) {
      if (edge.includes('w')) next.x -= minWidth - next.width;
      next.width = minWidth;
    }
    if (next.height < minHeight) {
      if (edge.includes('n')) next.y -= minHeight - next.height;
      next.height = minHeight;
    }
    next = constrainDesktopLyricsBounds(next);
    desktopLyricsProgrammaticMove = true;
    desktopLyricsWindow.setBounds(next, false);
    desktopLyricsUserBounds = desktopLyricsWindow.getBounds();
    scheduleDesktopLyricsUserBoundsSave();
    setTimeout(() => {
      desktopLyricsProgrammaticMove = false;
    }, 120);
    return { ok: true, bounds: desktopLyricsUserBounds };
  } catch (e) {
    return { ok: false, error: e.message || 'DESKTOP_LYRICS_RESIZE_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-set-enabled', async (_event, enabled, payload) => {
  wallpaperDebugLog('ipc-set-enabled', {
    enabled: !!enabled,
    payloadEnabled: payload && payload.enabled,
    hasTexture: !!(payload && payload.texture),
  });
  try {
    if (USE_WALLPAPER_ENGINE_WEB_BRIDGE) {
      wallpaperState = { ...mergeWallpaperPayloadPreservingBackground(wallpaperState, payload || {}), enabled: !!enabled };
      startWallpaperEngineBridgeServer();
      let overlayProject = null;
      if (enabled) {
        overlayProject = await activateMineradioWallpaperEngineOverlayProject({ captureScene: true });
        if (!overlayProject || overlayProject.ok === false) {
          wallpaperDebugLog('we-overlay-activate-failed', {
            error: overlayProject && overlayProject.error || 'UNKNOWN',
          });
          wallpaperState = { ...wallpaperState, enabled: false };
          broadcastWallpaperEnabledState(false);
          broadcastWallpaperEngineBridgeState();
          return {
            ok: false,
            enabled: false,
            bridge: true,
            error: overlayProject && overlayProject.error || 'WALLPAPER_ENGINE_OVERLAY_FAILED',
            overlayProject,
            port: wallpaperEngineBridgePort,
          };
        }
        closeWallpaperWindow({ persistDisabled: false });
        stopWallpaperEngineConfigWatchers();
        updateWallpaperMainWindowSuspension('we-bridge-enabled');
      } else {
        restoreWallpaperEngineOriginalFromOverlay();
        closeWallpaperWindow({ persistDisabled: true });
        stopWallpaperEngineConfigWatchers();
        updateWallpaperMainWindowSuspension('we-bridge-disabled');
      }
      broadcastWallpaperEnabledState(!!enabled);
      broadcastWallpaperEngineBridgeState();
      wallpaperDebugLog('we-bridge-set-enabled-ok', {
        enabled: !!enabled,
        port: wallpaperEngineBridgePort,
        overlayProject: enabled ? !!(overlayProject && overlayProject.ok) : false,
      });
      return {
        ok: true,
        enabled: !!enabled,
        bridge: true,
        overlayProject: enabled ? overlayProject : null,
        port: wallpaperEngineBridgePort,
      };
    }
    if (enabled) {
      const win = createWallpaperWindow(payload || {});
      try {
        await waitForWallpaperWindowLoaded(win, 8000);
      } catch (loadError) {
        wallpaperDebugLog('ipc-set-enabled-load-timeout', { error: loadError.message || String(loadError) });
        closeWallpaperWindow({ persistDisabled: true });
        broadcastWallpaperEnabledState(false);
        return { ok: false, enabled: false, error: loadError.message || 'WALLPAPER_LOAD_TIMEOUT' };
      }
      showAndAttachWallpaperWindow();
      updateWallpaperMainWindowSuspension('wallpaper-enabled');
      broadcastWallpaperEnabledState(true);
      wallpaperDebugLog('ipc-set-enabled-ok', {
        enabled: true,
        bounds: win && !win.isDestroyed() ? win.getBounds() : null,
      });
      return { ok: true, enabled: true, bounds: win && !win.isDestroyed() ? win.getBounds() : null };
    }
    closeWallpaperWindow({ persistDisabled: true });
    broadcastWallpaperEnabledState(false);
    wallpaperDebugLog('ipc-set-enabled-ok', { enabled: false });
    return { ok: true, enabled: false };
  } catch (e) {
    wallpaperDebugLog('ipc-set-enabled-error', { error: e.message || String(e) });
    return { ok: false, error: e.message || 'WALLPAPER_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-update', async (_event, payload) => {
  try {
    const shouldPersistUpdate = !!(payload && payload.__persist);
    const previousPreset = wallpaperPresetForRenderer(wallpaperState);
    const normalizedPayload = normalizeWallpaperPayload(payload || {});
    delete normalizedPayload.__persist;
    const nextWallpaperState = mergeWallpaperPayloadPreservingBackground(wallpaperState, normalizedPayload);
    const nextPreset = wallpaperPresetForRenderer(nextWallpaperState);
    if (previousPreset !== nextPreset || shouldPersistUpdate) {
      wallpaperDebugLog('wallpaper-update-preset', {
        previousPreset,
        nextPreset,
        enabled: !!nextWallpaperState.enabled,
        persist: shouldPersistUpdate,
        renderer: shouldUseNativeD3D11Wallpaper(nextWallpaperState) ? 'native-d3d11' : (shouldUseWebglSonicWallpaper(nextWallpaperState) ? 'webgl-sonic' : 'webgl'),
      });
    }
    wallpaperState = nextWallpaperState;
    if (USE_WALLPAPER_ENGINE_WEB_BRIDGE) {
      startWallpaperEngineBridgeServer();
      stopWallpaperEngineConfigWatchers();
      updateWallpaperMainWindowSuspension('we-bridge-update');
      broadcastWallpaperEngineBridgeState();
      return { ok: true, enabled: !!wallpaperState.enabled, bridge: true, port: wallpaperEngineBridgePort };
    }
    if (wallpaperState.enabled) {
      const win = wallpaperWindow && !wallpaperWindow.isDestroyed() ? wallpaperWindow : null;
      const wantsNativeWallpaper = process.platform === 'win32'
        && (USE_NATIVE_WALLPAPER || shouldUseNativeD3D11Wallpaper(wallpaperState));
      const hasRendererMismatch = !!(win && !!win.nativeWallpaper !== !!wantsNativeWallpaper);
      if (!win || hasRendererMismatch) {
        wallpaperDebugLog('update-restart-renderer', {
          enabled: true,
          hasWindow: !!win,
          currentNative: !!(win && win.nativeWallpaper),
          wantsNative: !!wantsNativeWallpaper,
          preset: wallpaperPresetForRenderer(wallpaperState),
        });
        if (win) closeWallpaperWindow({ persistDisabled: false });
        const nextWin = createWallpaperWindow(wallpaperState);
        if (nextWin && !nextWin.nativeWallpaper) {
          showAndAttachWallpaperWindow();
          sendWallpaperStateSoon(120, { persist: shouldPersistUpdate });
        } else {
          sendWallpaperStateSoon(500, { persist: shouldPersistUpdate });
        }
        return { ok: true, enabled: true, restarted: true, renderer: nextWin && nextWin.nativeWallpaper ? 'native' : 'webgl' };
      }
      if (!win) {
        wallpaperDebugLog('update-skip-no-window', { enabled: true });
        return { ok: false, enabled: false, error: 'WALLPAPER_WINDOW_NOT_READY' };
      }
      sendWallpaperState({ persist: shouldPersistUpdate });
      startWallpaperMousePoller();
      if (!shouldUseNativeD3D11Wallpaper(wallpaperState)) startWallpaperEngineConfigWatcher();
      return { ok: true, enabled: true, bounds: win && !win.isDestroyed() ? win.getBounds() : null };
    } else if (wallpaperWindow && !wallpaperWindow.isDestroyed()) {
      sendWallpaperState({ persist: shouldPersistUpdate });
      stopWallpaperEngineConfigWatchers();
    } else {
      stopWallpaperEngineConfigWatchers();
    }
    return { ok: true, enabled: !!wallpaperState.enabled };
  } catch (e) {
    return { ok: false, error: e.message || 'WALLPAPER_UPDATE_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-get-state', async () => {
  const hasWindow = !!(wallpaperWindow && !wallpaperWindow.isDestroyed());
  if (USE_WALLPAPER_ENGINE_WEB_BRIDGE) {
    return {
      ok: true,
      enabled: !!(wallpaperState && wallpaperState.enabled),
      bridge: true,
      port: wallpaperEngineBridgePort,
      hasWindow,
      visible: hasWindow ? wallpaperWindow.isVisible() : false,
      bounds: hasWindow ? wallpaperWindow.getBounds() : null,
    };
  }
  return {
    ok: true,
    enabled: !!(wallpaperState && wallpaperState.enabled && hasWindow),
    hasWindow,
    visible: hasWindow ? wallpaperWindow.isVisible() : false,
    bounds: hasWindow ? wallpaperWindow.getBounds() : null,
  };
});

ipcMain.handle('mineradio-wallpaper-engine-start', async () => {
  try {
    await shell.openExternal(WALLPAPER_ENGINE_STEAM_URL);
    if (wallpaperState && wallpaperState.enabled) {
      setTimeout(refreshWallpaperEngineConfigWatchers, 3000);
      setTimeout(refreshWallpaperEngineConfigWatchers, 9000);
    }
    return { ok: true, url: WALLPAPER_ENGINE_STEAM_URL };
  } catch (e) {
    return { ok: false, error: e.message || 'WALLPAPER_ENGINE_START_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-texture-choose', async (event) => {
  try {
    const owner = getSenderWindow(event);
    const result = await dialog.showOpenDialog(owner, {
      title: '选择 Wallpaper Engine 原文件作为粒子纹理',
      properties: ['openFile'],
      filters: [
        { name: 'Images', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'avif'] },
      ],
    });
    if (result.canceled || !result.filePaths || !result.filePaths[0]) return { ok: false, canceled: true };
    const filePath = result.filePaths[0];
    const ext = path.extname(filePath).toLowerCase();
    if (!WALLPAPER_TEXTURE_IMAGE_EXTS.has(ext)) {
      return { ok: false, error: 'UNSUPPORTED_WALLPAPER_TEXTURE_TYPE' };
    }
    return {
      ok: true,
      filePath,
      fileUrl: pathToFileURL(filePath).href,
      type: 'image',
      name: path.basename(filePath),
    };
  } catch (e) {
    return { ok: false, error: e.message || 'WALLPAPER_TEXTURE_CHOOSE_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-engine-texture-current', async () => {
  try {
    return await resolveWallpaperEngineCurrentTexture({ captureScene: false });
  } catch (e) {
    return { ok: false, error: e.message || 'WALLPAPER_ENGINE_TEXTURE_RESOLVE_FAILED' };
  }
});

ipcMain.handle('mineradio-wallpaper-engine-follow-watch', async (_event, enabled) => {
  wallpaperEnginePlayerFollowWatchEnabled = !!enabled;
  if (wallpaperEnginePlayerFollowWatchEnabled) {
    startWallpaperEngineConfigWatcher();
  } else if (!wallpaperEngineShouldWatchConfig()) {
    stopWallpaperEngineConfigWatchers();
  }
  return { ok: true, enabled: wallpaperEnginePlayerFollowWatchEnabled };
});

ipcMain.handle('mineradio-system-audio-capture-source', async () => {
  try {
    if (!desktopCapturer || process.platform !== 'win32') return { ok: false, error: 'UNSUPPORTED_PLATFORM' };
    const primaryDisplay = screen.getPrimaryDisplay();
    const sources = await desktopCapturer.getSources({
      types: ['screen'],
      thumbnailSize: { width: 1, height: 1 },
      fetchWindowIcons: false,
    });
    if (!sources || !sources.length) return { ok: false, error: 'NO_SCREEN_SOURCE' };
    const primaryId = primaryDisplay && primaryDisplay.id != null ? String(primaryDisplay.id) : '';
    const source = sources.find((item) => String(item.display_id || '') === primaryId) || sources[0];
    return {
      ok: true,
      id: source.id,
      name: source.name || 'Screen',
      displayId: source.display_id || primaryId,
    };
  } catch (e) {
    return { ok: false, error: e.message || 'SYSTEM_AUDIO_SOURCE_FAILED' };
  }
});

function parseHttpProxyResolution(resolution) {
  const rules = String(resolution || '').split(';').map(rule => rule.trim()).filter(Boolean);
  for (const rule of rules) {
    const match = rule.match(/^(PROXY|HTTP|HTTPS)\s+(.+)$/i);
    if (!match) continue;
    const raw = match[2].trim();
    try {
      const url = new URL(/^https?:\/\//i.test(raw) ? raw : `http://${raw}`);
      if (url.hostname && url.port) return url.toString();
    } catch (e) {}
  }
  return '';
}

function addNoProxyHost(value, host) {
  const entries = String(value || '').split(',').map(item => item.trim()).filter(Boolean);
  if (!entries.some(item => item.toLowerCase() === host.toLowerCase())) entries.push(host);
  return entries.join(',');
}

async function configureLocalServerProxy() {
  if (process.env.HTTPS_PROXY || process.env.https_proxy) return;
  try {
    const resolution = await session.defaultSession.resolveProxy('https://u.y.qq.com/cgi-bin/musicu.fcg');
    const proxyUrl = parseHttpProxyResolution(resolution);
    if (!proxyUrl) return;
    process.env.HTTPS_PROXY = proxyUrl;
    process.env.HTTP_PROXY = process.env.HTTP_PROXY || proxyUrl;
    process.env.NO_PROXY = addNoProxyHost(addNoProxyHost(process.env.NO_PROXY, '127.0.0.1'), 'localhost');
    console.log('[Proxy] local server using Electron system proxy:', new URL(proxyUrl).host);
  } catch (e) {
    console.warn('[Proxy] system proxy resolution failed:', e.message);
  }
}

async function createWindow() {
  htmlFullscreenActive = false;
  windowFullscreenActive = false;
  const port = await findOpenPort(3000);
  mainServerPort = port;

  process.env.HOST = '127.0.0.1';
  process.env.PORT = String(port);
  process.env.COOKIE_FILE = path.join(app.getPath('userData'), '.cookie');
  process.env.QQ_COOKIE_FILE = path.join(app.getPath('userData'), '.qq-cookie');
  process.env.MINERADIO_UPDATE_DIR = getUpdateDownloadDir();
  await configureLocalServerProxy();
  try {
    const legacyQQCookie = path.join(__dirname, '..', '.qq-cookie');
    if (fs.existsSync(legacyQQCookie)) {
      if (!fs.existsSync(process.env.QQ_COOKIE_FILE)) {
        fs.copyFileSync(legacyQQCookie, process.env.QQ_COOKIE_FILE);
      }
      fs.unlinkSync(legacyQQCookie);
    }
  } catch (e) {
    console.warn('QQ cookie migration skipped:', e.message);
  }

  localServer = require(path.join(__dirname, '..', 'server.js'));
  await waitForServer(localServer);
  if (USE_WALLPAPER_ENGINE_WEB_BRIDGE) startWallpaperEngineBridgeServer();

  const savedWindowState = readDesktopWindowState();
  const initialBounds = savedWindowState.bounds || getWindowedBounds();

  mainWindow = new BrowserWindow({
    ...initialBounds,
    minWidth: 960,
    minHeight: 540,
    show: false,
    frame: false,
    fullscreen: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: true,
    autoHideMenuBar: true,
    title: APP_NAME,
    icon: APP_ICON_ICO,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      backgroundThrottling: true,
      webSecurity: false,
    },
  });

  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });

  mainWindow.webContents.once('did-finish-load', () => {
    sendWindowState(mainWindow);
  });

  mainWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && (input.key === 'Escape' || input.code === 'Escape') && mainWindow.isFullScreen()) {
      event.preventDefault();
      exitFullscreenToWindow(mainWindow);
    }
  });

  mainWindow.once('ready-to-show', () => {
    if (savedWindowState.maximized && !mainWindow.isMaximized()) {
      try { mainWindow.maximize(); } catch (_) {}
    }
    mainWindow.show();
    sendWindowState(mainWindow);
  });

  mainWindow.on('maximize', () => { scheduleDesktopWindowStateSave(mainWindow, 80); sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('maximize'); });
  mainWindow.on('unmaximize', () => { scheduleDesktopWindowStateSave(mainWindow, 80); sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('unmaximize'); });
  mainWindow.on('minimize', () => { sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('minimize'); });
  mainWindow.on('restore', () => { sendWindowRestoreAnimation(mainWindow); sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('restore'); });
  mainWindow.on('show', () => { sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('show'); });
  mainWindow.on('hide', () => { sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('hide'); });
  mainWindow.on('focus', () => { sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('focus'); });
  mainWindow.on('blur', () => { sendWindowState(mainWindow); updateTrayMenu(); updateWallpaperMainWindowSuspension('blur'); });
  mainWindow.on('move', () => { scheduleWindowStateSend(mainWindow); scheduleDesktopWindowStateSave(mainWindow); });
  mainWindow.on('resize', () => { scheduleWindowStateSend(mainWindow); scheduleDesktopWindowStateSave(mainWindow); });
  mainWindow.on('close', (event) => {
    if (isQuitting) return;
    if (!readDesktopSettings().closeToTray) {
      pausePlaybackBeforeQuit('native-close');
      isQuitting = true;
      return;
    }
    event.preventDefault();
    hideMainWindowToBackground(mainWindow);
  });
  mainWindow.on('closed', () => {
    if (mainWindowStateTimer) {
      clearTimeout(mainWindowStateTimer);
      mainWindowStateTimer = null;
    }
    if (mainWindowStateSaveTimer) {
      clearTimeout(mainWindowStateSaveTimer);
      mainWindowStateSaveTimer = null;
    }
    closeOverlayWindows();
    mainWindow = null;
    updateTrayMenu();
  });
  mainWindow.on('enter-full-screen', () => {
    if (!mainWindowPreFullscreenBounds) rememberPreFullscreenBounds(mainWindow);
    windowFullscreenActive = true;
    sendWindowState(mainWindow);
    updateTrayMenu();
  });
  mainWindow.on('leave-full-screen', () => {
    windowFullscreenActive = false;
    setTimeout(() => applyFullscreenRestoreBounds(mainWindow), 50);
    sendWindowState(mainWindow);
    updateTrayMenu();
  });
  mainWindow.on('enter-html-full-screen', () => {
    htmlFullscreenActive = true;
    sendWindowState(mainWindow);
    updateTrayMenu();
  });
  mainWindow.on('leave-html-full-screen', () => {
    htmlFullscreenActive = false;
    setTimeout(() => applyWindowedBounds(mainWindow), 50);
    sendWindowState(mainWindow);
    updateTrayMenu();
  });

  await mainWindow.loadURL(`http://127.0.0.1:${port}`);
}

app.setName(APP_NAME);
if (process.platform === 'win32') app.setAppUserModelId(APP_USER_MODEL_ID);

if (!gotSingleInstanceLock) {
  app.quit();
} else {
  app.on('second-instance', (_event, argv) => {
    const playerCommand = playerCommandFromArgs(argv);
    const wallpaperPreset = wallpaperPresetFromArgs(argv);
    const wallpaperCommand = wallpaperCommandFromArgs(argv);
    if (Number.isFinite(wallpaperPreset) || wallpaperCommand || playerCommand) {
      (async () => {
        if (Number.isFinite(wallpaperPreset)) await handleWallpaperPresetArgs(argv, 'second-instance');
        if (wallpaperCommand) await handleWallpaperCommandArgs(argv, 'second-instance');
        if (playerCommand) await handlePlayerCommandArgs(argv, 'second-instance');
      })().catch((e) => {
        console.error('Second instance command failed:', e);
      });
      return;
    }
    if (!focusMainWindow()) {
      app.whenReady().then(() => createWindow()).catch((e) => console.error('Second instance window restore failed:', e));
    }
  });

  app.whenReady().then(async () => {
    if (!RESTORE_WALLPAPER_ON_STARTUP) clearSavedWallpaperEnabledState('startup-auto-restore-disabled');
    ensureTray();
    screen.on('display-metrics-changed', () => {
      if (mainWindow && !mainWindow.isDestroyed() && !mainWindow.isMaximized() && !mainWindow.isFullScreen()) {
        const safeBounds = clampWindowBoundsToDisplays(mainWindow.getBounds());
        if (safeBounds && !boundsCloseTo(safeBounds, mainWindow.getBounds())) mainWindow.setBounds(safeBounds, false);
      }
      refreshDesktopLyricsBoundsForDisplays();
      positionWallpaperWindow();
      if (wallpaperWindow && !wallpaperWindow.isDestroyed()) {
        if (wallpaperWindow.nativeWallpaper) sendWallpaperState({ persist: false });
        else attachWallpaperToWorkerW(wallpaperWindow);
      }
      scheduleWindowStateSend(mainWindow);
    });
    screen.on('display-added', () => {
      refreshDesktopLyricsBoundsForDisplays();
      scheduleWindowStateSend(mainWindow);
    });
    screen.on('display-removed', () => {
      refreshDesktopLyricsBoundsForDisplays();
      scheduleWindowStateSend(mainWindow);
    });
    await createWindow();
    await handleWallpaperPresetArgs(process.argv, 'startup');
    await handleWallpaperCommandArgs(process.argv, 'startup');
    await handlePlayerCommandArgs(process.argv, 'startup');
    if (RESTORE_WALLPAPER_ON_STARTUP) restoreWallpaperWindowFromSavedState();
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
    else focusMainWindow();
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });

  app.on('before-quit', () => {
    pausePlaybackBeforeQuit('before-quit');
    isQuitting = true;
    if (mainWindow && !mainWindow.isDestroyed()) writeDesktopWindowStateFor(mainWindow);
    unregisterMineradioGlobalHotkeys();
    stopWallpaperEngineConfigWatchers();
    stopWallpaperEngineBridgeServer();
    closeTrayMenuWindow();
    closeOverlayWindows();
    if (localServer && localServer.close) localServer.close();
    if (tray && !tray.isDestroyed()) tray.destroy();
    tray = null;
  });
}
