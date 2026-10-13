const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('desktopWindow', {
  isDesktop: true,
  minimize: () => ipcRenderer.invoke('desktop-window-minimize'),
  toggleMaximize: () => ipcRenderer.invoke('desktop-window-toggle-maximize'),
  toggleFullscreen: () => ipcRenderer.invoke('desktop-window-toggle-fullscreen'),
  exitFullscreenWindowed: () => ipcRenderer.invoke('desktop-window-exit-fullscreen-windowed'),
  getState: () => ipcRenderer.invoke('desktop-window-get-state'),
  beginWindowDrag: (payload) => ipcRenderer.send('desktop-window-drag-start-fast', payload || {}),
  updateWindowDrag: (payload) => ipcRenderer.send('desktop-window-drag-move-fast', payload || {}),
  finishWindowDrag: () => ipcRenderer.send('desktop-window-drag-end-fast'),
  beginWindowResize: (payload) => ipcRenderer.send('desktop-window-resize-start-fast', payload || {}),
  updateWindowResize: (payload) => ipcRenderer.send('desktop-window-resize-move-fast', payload || {}),
  finishWindowResize: () => ipcRenderer.send('desktop-window-resize-end-fast'),
  startWindowDrag: () => ipcRenderer.invoke('desktop-window-drag-start'),
  moveWindowDrag: () => ipcRenderer.invoke('desktop-window-drag-move'),
  endWindowDrag: () => ipcRenderer.invoke('desktop-window-drag-end'),
  close: () => ipcRenderer.invoke('desktop-window-close'),
  openNeteaseMusicLogin: () => ipcRenderer.invoke('netease-music-open-login'),
  clearNeteaseMusicLogin: () => ipcRenderer.invoke('netease-music-clear-login'),
  openQQMusicLogin: (options) => ipcRenderer.invoke('qq-music-open-login', options || {}),
  clearQQMusicLogin: () => ipcRenderer.invoke('qq-music-clear-login'),
  openUpdateInstaller: (filePath) => ipcRenderer.invoke('mineradio-open-update-installer', filePath),
  restartApp: () => ipcRenderer.invoke('mineradio-restart-app'),
  getDesktopSettings: () => ipcRenderer.invoke('mineradio-desktop-settings-get'),
  setDesktopSettings: (patch) => ipcRenderer.invoke('mineradio-desktop-settings-set', patch || {}),
  updateTrayPlayerState: (state) => ipcRenderer.invoke('mineradio-tray-player-state', state || {}),
  configureGlobalHotkeys: (bindings) => ipcRenderer.invoke('mineradio-hotkeys-configure-global', bindings || []),
  exportJsonFile: (payload) => ipcRenderer.invoke('mineradio-export-json-file', payload || {}),
  importJsonFile: () => ipcRenderer.invoke('mineradio-import-json-file'),
  chooseLocalAudioFiles: () => ipcRenderer.invoke('mineradio-local-audio-files-choose'),
  chooseLocalAudioFolder: (options) => ipcRenderer.invoke('mineradio-local-audio-folder-choose', options || {}),
  scanLocalAudioFolder: (folderPath, options) => ipcRenderer.invoke('mineradio-local-audio-folder-scan', folderPath, options || {}),
  onGlobalHotkey: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('mineradio-global-hotkey', listener);
    return () => ipcRenderer.removeListener('mineradio-global-hotkey', listener);
  },
  setDesktopLyricsEnabled: (enabled, payload) => ipcRenderer.invoke('mineradio-desktop-lyrics-set-enabled', !!enabled, payload || {}),
  updateDesktopLyrics: (payload) => {
    ipcRenderer.send('mineradio-desktop-lyrics-update-fast', payload || {});
    return Promise.resolve({ ok: true });
  },
  onDesktopLyricsLockState: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('mineradio-desktop-lyrics-lock-state', listener);
    return () => ipcRenderer.removeListener('mineradio-desktop-lyrics-lock-state', listener);
  },
  onDesktopLyricsEnabledState: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('mineradio-desktop-lyrics-enabled-state', listener);
    return () => ipcRenderer.removeListener('mineradio-desktop-lyrics-enabled-state', listener);
  },
  onWallpaperEnabledState: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('mineradio-wallpaper-enabled-state', listener);
    return () => ipcRenderer.removeListener('mineradio-wallpaper-enabled-state', listener);
  },
  setWallpaperMode: (enabled, payload) => ipcRenderer.invoke('mineradio-wallpaper-set-enabled', !!enabled, payload || {}),
  updateWallpaperMode: (payload) => ipcRenderer.invoke('mineradio-wallpaper-update', payload || {}),
  getWallpaperModeState: () => ipcRenderer.invoke('mineradio-wallpaper-get-state'),
  startWallpaperEngine: () => ipcRenderer.invoke('mineradio-wallpaper-engine-start'),
  chooseWallpaperTexture: () => ipcRenderer.invoke('mineradio-wallpaper-texture-choose'),
  resolveWallpaperEngineTexture: () => ipcRenderer.invoke('mineradio-wallpaper-engine-texture-current'),
  setWallpaperEngineFollowWatch: (enabled) => ipcRenderer.invoke('mineradio-wallpaper-engine-follow-watch', !!enabled),
  getSystemAudioCaptureSource: () => ipcRenderer.invoke('mineradio-system-audio-capture-source'),
  onWallpaperEngineTextureChanged: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('mineradio-wallpaper-engine-texture-changed', listener);
    return () => ipcRenderer.removeListener('mineradio-wallpaper-engine-texture-changed', listener);
  },
  onStateChange: (callback) => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('desktop-window-state', listener);
    return () => ipcRenderer.removeListener('desktop-window-state', listener);
  },
  onRestoreAnimation: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, payload) => callback(payload || {});
    ipcRenderer.on('desktop-window-restore-animation', listener);
    return () => ipcRenderer.removeListener('desktop-window-restore-animation', listener);
  },
});

window.addEventListener('DOMContentLoaded', () => {
  document.documentElement.classList.add('desktop-shell-root');
  document.body.classList.add('desktop-shell');
});
