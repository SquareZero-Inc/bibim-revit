import { useState, useCallback, useEffect } from 'react';
import { sendToBackend, onBackendMessage } from '../bridge';
import type { CodeSnippetInfo, CodeSnippetDetail, CodeFolder } from '../types';

export function useCodeLibrary() {
  const [codeSnippets, setCodeSnippets] = useState<CodeSnippetInfo[]>([]);
  const [codeFolders, setCodeFolders] = useState<CodeFolder[]>([]);
  const [selectedSnippet, setSelectedSnippet] = useState<CodeSnippetDetail | null>(null);

  useEffect(() => {
    onBackendMessage('code_library', (payload) => {
      if (Array.isArray(payload)) {
        setCodeSnippets(payload as CodeSnippetInfo[]);
      } else {
        const data = payload as { folders?: CodeFolder[]; snippets?: CodeSnippetInfo[] };
        setCodeFolders(data.folders ?? []);
        setCodeSnippets(data.snippets ?? []);
      }
    });

    onBackendMessage('code_snippet', (payload) => {
      setSelectedSnippet(payload as CodeSnippetDetail);
    });

    onBackendMessage('code_library_updated', () => {
      sendToBackend('get_code_library', {});
    });

    sendToBackend('get_code_library', {});
  }, []);

  const fetchCodeSnippet = useCallback((id: string) => {
    sendToBackend('get_code_snippet', { id });
  }, []);

  const deleteCodeSnippet = useCallback((id: string) => {
    sendToBackend('delete_code_snippet', { id });
    setSelectedSnippet(null);
  }, []);

  const clearSelectedSnippet = useCallback(() => setSelectedSnippet(null), []);

  const renameSnippet = useCallback((id: string, title: string) => {
    sendToBackend('rename_snippet', { id, title });
  }, []);

  const moveSnippet = useCallback((snippetId: string, folderId: string | null) => {
    sendToBackend('move_snippet', { snippetId, folderId });
  }, []);

  const createFolder = useCallback((name: string) => {
    sendToBackend('create_folder', { name });
  }, []);

  const renameFolder = useCallback((id: string, name: string) => {
    sendToBackend('rename_folder', { id, name });
  }, []);

  const deleteFolder = useCallback((id: string) => {
    sendToBackend('delete_folder', { id });
  }, []);

  return {
    codeSnippets,
    codeFolders,
    selectedSnippet,
    fetchCodeSnippet,
    deleteCodeSnippet,
    clearSelectedSnippet,
    renameSnippet,
    moveSnippet,
    createFolder,
    renameFolder,
    deleteFolder,
  };
}
