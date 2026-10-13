const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('trayMenu', {
  getState: () => ipcRenderer.invoke('mineradio-tray-menu-get-state'),
  action: (action) => ipcRenderer.invoke('mineradio-tray-menu-action', action),
  close: () => ipcRenderer.invoke('mineradio-tray-menu-close'),
  resizeToContent: (height) => ipcRenderer.invoke('mineradio-tray-menu-resize-to-content', height),
  onState: (callback) => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, state) => callback(state || {});
    ipcRenderer.on('mineradio-tray-menu-state', listener);
    return () => ipcRenderer.removeListener('mineradio-tray-menu-state', listener);
  },
});
