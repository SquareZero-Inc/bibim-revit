import { useState, useCallback, useEffect } from 'react';
import { sendToBackend, onBackendMessage } from '../bridge';
import { setUiLanguage, type UiLanguage } from '../i18n';
import type { UpdateInfo } from '../types';

export type DownloadState = 'idle' | 'downloading' | 'complete';

export function useAppInfo() {
  const [appVersion, setAppVersion] = useState('');
  const [appLanguage, setAppLanguage] = useState<UiLanguage>('kr');
  const [revitVersion, setRevitVersion] = useState('');
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);
  const [downloadState, setDownloadState] = useState<DownloadState>('idle');
  const [downloadFolderPath, setDownloadFolderPath] = useState<string>('');

  useEffect(() => {
    onBackendMessage('app_info', (payload) => {
      const info = payload as {
        version?: string;
        language?: string;
        revitVersion?: string;
        updateAvailable?: boolean;
        updateMandatory?: boolean;
        latestVersion?: string;
        downloadUrl?: string;
        releaseNotes?: string;
      };
      if (info?.version) setAppVersion(info.version);
      if (info?.revitVersion) setRevitVersion(info.revitVersion);
      const language = setUiLanguage(info?.language);
      setAppLanguage(language);
      if (info?.updateAvailable) {
        setUpdateInfo({
          updateAvailable: true,
          isMandatory: info.updateMandatory ?? false,
          currentVersion: info.version ?? '',
          latestVersion: info.latestVersion ?? '',
          downloadUrl: info.downloadUrl,
          releaseNotes: info.releaseNotes,
        });
      }
    });

    onBackendMessage('update_info', (payload) => {
      const info = payload as UpdateInfo;
      if (info?.updateAvailable) setUpdateInfo(info);
    });

    onBackendMessage('download_progress', () => {
      setDownloadState('downloading');
    });

    onBackendMessage('download_complete', (payload) => {
      const data = payload as { folderPath?: string };
      setDownloadState('complete');
      setDownloadFolderPath(data?.folderPath ?? '');
    });

    sendToBackend('get_app_info', {});
  }, []);

  const dismissUpdate = useCallback(() => setUpdateInfo(null), []);

  const startDownloadUpdate = useCallback((url: string, version: string) => {
    setDownloadState('downloading');
    sendToBackend('download_update', { url, version });
  }, []);

  const openDownloadFolder = useCallback((path: string) => {
    sendToBackend('open_folder', { path });
  }, []);

  return {
    appVersion, appLanguage, revitVersion, updateInfo, dismissUpdate,
    downloadState, downloadFolderPath, startDownloadUpdate, openDownloadFolder,
  };
}
