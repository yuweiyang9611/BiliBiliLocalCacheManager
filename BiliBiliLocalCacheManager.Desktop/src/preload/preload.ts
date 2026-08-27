import { contextBridge, ipcRenderer } from 'electron';
import type { AppSettings, CacheManagerApi, HostProgress, PlayerPreference, SearchRequest, SelectionTarget } from '../shared/contracts';
import { channels } from '../shared/channels';

const api: CacheManagerApi = {
  health: () => ipcRenderer.invoke(channels.health),
  getInitialState: () => ipcRenderer.invoke(channels.initialState),
  getSettings: () => ipcRenderer.invoke(channels.settingsGet),
  updateSettings: (patch: Partial<AppSettings>) => ipcRenderer.invoke(channels.settingsUpdate, patch),
  chooseRootDirectory: (defaultPath?: string) => ipcRenderer.invoke(channels.chooseRoot, defaultPath),
  scan: (options: { rootPath: string; includeIncomplete: boolean }) => ipcRenderer.invoke(channels.scan, options),
  cancel: () => ipcRenderer.invoke(channels.cancel),
  search: (request: SearchRequest) => ipcRenderer.invoke(channels.search, request),
  getStorage: () => ipcRenderer.invoke(channels.storageGet),
  cleanupTranscodeCache: () => ipcRenderer.invoke(channels.artifactsCleanup),
  clearTranscodeCache: () => ipcRenderer.invoke(channels.artifactsClear),
  openTranscodeCache: () => ipcRenderer.invoke(channels.artifactsOpen),
  moveToTrash: (avids: string[]) => ipcRenderer.invoke(channels.trashMove, avids),
  listTrash: () => ipcRenderer.invoke(channels.trashList),
  restoreTrash: (entryIds: string[]) => ipcRenderer.invoke(channels.trashRestore, entryIds),
  purgeTrash: (entryIds?: string[]) => ipcRenderer.invoke(channels.trashPurge, entryIds),
  play: (targets: SelectionTarget[], playerPreference: PlayerPreference) => ipcRenderer.invoke(channels.play, targets, playerPreference),
  exportMedia: (targets: SelectionTarget[], suggestedName: string) => ipcRenderer.invoke(channels.exportMedia, targets, suggestedName),
  exportDiagnostics: (suggestedName: string) => ipcRenderer.invoke(channels.exportDiagnostics, suggestedName),
  getDesktopInfo: () => ipcRenderer.invoke(channels.desktopInfo),
  onProgress: (listener: (progress: HostProgress) => void) => subscribe<HostProgress>(channels.progress, listener),
  onHostUnavailable: (listener: (message: string) => void) => subscribe<string>(channels.unavailable, listener),
};

function subscribe<T>(channel: string, listener: (value: T) => void): () => void {
  const wrapped = (_event: Electron.IpcRendererEvent, value: T) => listener(value);
  ipcRenderer.on(channel, wrapped);
  return () => ipcRenderer.removeListener(channel, wrapped);
}

contextBridge.exposeInMainWorld('cacheManager', Object.freeze(api));
