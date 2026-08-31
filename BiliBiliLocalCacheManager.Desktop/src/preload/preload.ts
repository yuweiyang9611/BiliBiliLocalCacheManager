import { contextBridge, ipcRenderer } from 'electron';
import type { AppSettings, CacheDetailsRequest, CacheManagerApi, HostProgress, PlayerPreference, SearchRequest, SelectionTarget } from '../shared/contracts';
import { channels } from '../shared/channels';

const api: CacheManagerApi = {
  health: () => ipcRenderer.invoke(channels.health),
  getInitialState: () => ipcRenderer.invoke(channels.initialState),
  getSettings: () => ipcRenderer.invoke(channels.settingsGet),
  updateSettings: (patch: Partial<AppSettings>) => ipcRenderer.invoke(channels.settingsUpdate, patch),
  chooseRootDirectory: (defaultPath?: string) => ipcRenderer.invoke(channels.chooseRoot, defaultPath),
  scan: (options: { rootPath: string; includeIncomplete: boolean; persistSettings?: boolean; offset?: number; pageSize?: number }) => ipcRenderer.invoke(channels.scan, options),
  cancel: () => ipcRenderer.invoke(channels.cancel),
  search: (request: SearchRequest) => ipcRenderer.invoke(channels.search, request),
  getCacheDetails: (request: CacheDetailsRequest) => ipcRenderer.invoke(channels.cacheDetails, request),
  cancelCacheDetails: () => ipcRenderer.invoke(channels.cacheDetailsCancel),
  getStorage: (rootPath?: string) => ipcRenderer.invoke(channels.storageGet, rootPath),
  cleanupTranscodeCache: () => ipcRenderer.invoke(channels.artifactsCleanup),
  clearTranscodeCache: () => ipcRenderer.invoke(channels.artifactsClear),
  openTranscodeCache: () => ipcRenderer.invoke(channels.artifactsOpen),
  moveToTrash: (rootPath: string, avids: string[]) => ipcRenderer.invoke(channels.trashMove, rootPath, avids),
  listTrash: (rootPath: string) => ipcRenderer.invoke(channels.trashList, rootPath),
  restoreTrash: (rootPath: string, entryIds: string[]) => ipcRenderer.invoke(channels.trashRestore, rootPath, entryIds),
  purgeTrash: (rootPath: string, entryIds: string[]) => ipcRenderer.invoke(channels.trashPurge, rootPath, entryIds),
  play: (rootPath: string, targets: SelectionTarget[], playerPreference: PlayerPreference, includeIncomplete: boolean) =>
    ipcRenderer.invoke(channels.play, rootPath, targets, playerPreference, includeIncomplete),
  exportMedia: (rootPath: string, targets: SelectionTarget[], suggestedName: string, includeIncomplete: boolean) =>
    ipcRenderer.invoke(channels.exportMedia, rootPath, targets, suggestedName, includeIncomplete),
  exportDiagnostics: (suggestedName: string, rootPath?: string) => ipcRenderer.invoke(channels.exportDiagnostics, suggestedName, rootPath),
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
