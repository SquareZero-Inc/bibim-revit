import React, { useState, type CSSProperties } from 'react';
import { formatDateShort, t } from '../i18n';
import type { CodeSnippetInfo, CodeFolder } from '../types';

interface Props {
  snippets: CodeSnippetInfo[];
  folders: CodeFolder[];
  onSelect: (id: string) => void;
  onClose: () => void;
  onRenameSnippet: (id: string, title: string) => void;
  onDeleteSnippet: (id: string) => void;
  onMoveSnippet: (snippetId: string, folderId: string | null) => void;
  onCreateFolder: (name: string) => void;
  onRenameFolder: (id: string, name: string) => void;
  onDeleteFolder: (id: string) => void;
}

export default function CodeLibraryPanel({
  snippets,
  folders,
  onSelect,
  onClose,
  onRenameSnippet,
  onDeleteSnippet,
  onMoveSnippet,
  onCreateFolder,
  onRenameFolder,
  onDeleteFolder,
}: Props) {
  const [search, setSearch] = useState('');
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  const q = search.trim().toLowerCase();

  // When searching, flatten and filter all snippets
  const searchResults = q
    ? snippets.filter((s) => (s.title || '').toLowerCase().includes(q))
    : null;

  const toggleFolder = (id: string) =>
    setCollapsed((prev) => ({ ...prev, [id]: !prev[id] }));

  // Snippets at root (no folder)
  const rootSnippets = snippets.filter((s) => !s.folderId);

  return (
    <div style={rootStyle}>
      {/* Header */}
      <div style={headerStyle}>
        <span style={{ fontSize: 'var(--text-sm)', fontWeight: 600 }}>{t('codeLibrary')}</span>
        <div style={{ display: 'flex', gap: 'var(--space-xs)', alignItems: 'center' }}>
          <button
            onClick={() => onCreateFolder(t('newFolder'))}
            style={iconBtnStyle}
            title={t('newFolder')}
          >
            📁+
          </button>
          <button onClick={onClose} style={iconBtnStyle} title={t('close')}>X</button>
        </div>
      </div>

      {/* Search */}
      <div style={{ padding: 'var(--space-xs) var(--space-md)' }}>
        <input
          type="text"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={t('searchLibrary')}
          style={searchInputStyle}
        />
      </div>

      {/* Content */}
      <div style={{ flex: 1, overflow: 'auto', padding: 'var(--space-xs) var(--space-sm)' }}>
        {snippets.length === 0 && !search && (
          <div style={emptyStyle}>{t('noSnippets')}</div>
        )}

        {/* Search results — flat list */}
        {searchResults && (
          <>
            {searchResults.length === 0 && (
              <div style={emptyStyle}>—</div>
            )}
            {searchResults.map((s) => (
              <SnippetItem
                key={s.id}
                snippet={s}
                folders={folders}
                onSelect={onSelect}
                onRename={onRenameSnippet}
                onDelete={onDeleteSnippet}
                onMove={onMoveSnippet}
              />
            ))}
          </>
        )}

        {/* Normal tree view */}
        {!searchResults && (
          <>
            {/* Folders */}
            {folders.map((folder) => {
              const folderSnippets = snippets.filter((s) => s.folderId === folder.id);
              const isOpen = !collapsed[folder.id];
              return (
                <FolderItem
                  key={folder.id}
                  folder={folder}
                  snippets={folderSnippets}
                  allFolders={folders}
                  isOpen={isOpen}
                  onToggle={() => toggleFolder(folder.id)}
                  onRenameFolder={onRenameFolder}
                  onDeleteFolder={onDeleteFolder}
                  onSelectSnippet={onSelect}
                  onRenameSnippet={onRenameSnippet}
                  onDeleteSnippet={onDeleteSnippet}
                  onMoveSnippet={onMoveSnippet}
                />
              );
            })}

            {/* Root-level snippets (uncategorized) */}
            {rootSnippets.length > 0 && (
              <div style={{ marginTop: folders.length > 0 ? 'var(--space-sm)' : 0 }}>
                {folders.length > 0 && (
                  <div style={sectionLabelStyle}>{t('uncategorized')}</div>
                )}
                {rootSnippets.map((s) => (
                  <SnippetItem
                    key={s.id}
                    snippet={s}
                    folders={folders}
                    onSelect={onSelect}
                    onRename={onRenameSnippet}
                    onDelete={onDeleteSnippet}
                    onMove={onMoveSnippet}
                  />
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

// --- FolderItem ---

interface FolderItemProps {
  folder: CodeFolder;
  snippets: CodeSnippetInfo[];
  allFolders: CodeFolder[];
  isOpen: boolean;
  onToggle: () => void;
  onRenameFolder: (id: string, name: string) => void;
  onDeleteFolder: (id: string) => void;
  onSelectSnippet: (id: string) => void;
  onRenameSnippet: (id: string, title: string) => void;
  onDeleteSnippet: (id: string) => void;
  onMoveSnippet: (snippetId: string, folderId: string | null) => void;
}

function FolderItem({
  folder, snippets, allFolders, isOpen, onToggle,
  onRenameFolder, onDeleteFolder,
  onSelectSnippet, onRenameSnippet, onDeleteSnippet, onMoveSnippet,
}: FolderItemProps) {
  const [hovered, setHovered] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const handleRenameStart = (e: React.MouseEvent) => {
    e.stopPropagation();
    setRenameValue(folder.name);
    setRenaming(true);
    setConfirmingDelete(false);
  };

  const handleRenameSubmit = () => {
    const v = renameValue.trim();
    if (v) onRenameFolder(folder.id, v);
    setRenaming(false);
  };

  const handleDeleteConfirm = (e: React.MouseEvent) => {
    e.stopPropagation();
    onDeleteFolder(folder.id);
    setConfirmingDelete(false);
  };

  return (
    <div style={{ marginBottom: 'var(--space-xs)' }}>
      <div
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        style={{
          position: 'relative',
          display: 'flex',
          alignItems: 'center',
          gap: 'var(--space-xs)',
          padding: 'var(--space-xs) var(--space-sm)',
          borderRadius: 'var(--radius-md)',
          background: hovered ? 'var(--color-bg-hover)' : 'transparent',
          cursor: 'pointer',
        }}
      >
        <span onClick={onToggle} style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', userSelect: 'none' }}>
          {isOpen ? '▼' : '▶'}
        </span>
        <span onClick={onToggle} style={{ fontSize: 11 }}>📁</span>

        {renaming ? (
          <input
            autoFocus
            value={renameValue}
            onChange={(e) => setRenameValue(e.target.value)}
            onBlur={handleRenameSubmit}
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleRenameSubmit();
              if (e.key === 'Escape') setRenaming(false);
            }}
            onClick={(e) => e.stopPropagation()}
            style={inlineInputStyle}
          />
        ) : (
          <span
            onClick={onToggle}
            style={{
              flex: 1,
              fontSize: 'var(--text-sm)',
              fontWeight: 600,
              color: 'var(--color-text-primary)',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {folder.name}
            <span style={{ color: 'var(--color-text-muted)', fontWeight: 400, marginLeft: 4 }}>
              ({snippets.length})
            </span>
          </span>
        )}

        {hovered && !renaming && !confirmingDelete && (
          <div style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
            <button onClick={handleRenameStart} style={tinyIconBtn} title={t('renameFolder')}>✎</button>
            <button
              onClick={(e) => { e.stopPropagation(); setConfirmingDelete(true); }}
              style={{ ...tinyIconBtn, color: 'var(--color-error)' }}
              title={t('deleteFolder')}
            >✕</button>
          </div>
        )}
      </div>

      {confirmingDelete && (
        <div style={inlineConfirmStyle}>
          <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', flex: 1 }}>
            {t('confirmDeleteFolder')}
          </span>
          <button onClick={handleDeleteConfirm} style={{ ...tinyBtn, color: 'var(--color-error)' }}>
            {t('deleteFolder')}
          </button>
          <button onClick={() => setConfirmingDelete(false)} style={tinyBtn}>{t('cancelRename')}</button>
        </div>
      )}

      {isOpen && (
        <div style={{ paddingLeft: 20 }}>
          {snippets.length === 0 && (
            <div style={{ ...emptyStyle, padding: 'var(--space-xs) var(--space-sm)' }}>—</div>
          )}
          {snippets.map((s) => (
            <SnippetItem
              key={s.id}
              snippet={s}
              folders={allFolders}
              onSelect={onSelectSnippet}
              onRename={onRenameSnippet}
              onDelete={onDeleteSnippet}
              onMove={onMoveSnippet}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// --- SnippetItem ---

interface SnippetItemProps {
  snippet: CodeSnippetInfo;
  folders: CodeFolder[];
  onSelect: (id: string) => void;
  onRename: (id: string, title: string) => void;
  onDelete: (id: string) => void;
  onMove: (snippetId: string, folderId: string | null) => void;
}

function SnippetItem({ snippet, folders, onSelect, onRename, onDelete, onMove }: SnippetItemProps) {
  const [hovered, setHovered] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [showMoveMenu, setShowMoveMenu] = useState(false);

  const handleRenameStart = (e: React.MouseEvent) => {
    e.stopPropagation();
    setRenameValue(snippet.title || '');
    setRenaming(true);
    setConfirmingDelete(false);
    setShowMoveMenu(false);
  };

  const handleRenameSubmit = () => {
    const v = renameValue.trim();
    if (v) onRename(snippet.id, v);
    setRenaming(false);
  };

  const handleDeleteConfirm = (e: React.MouseEvent) => {
    e.stopPropagation();
    onDelete(snippet.id);
    setConfirmingDelete(false);
  };

  const handleMove = (e: React.MouseEvent, folderId: string | null) => {
    e.stopPropagation();
    onMove(snippet.id, folderId);
    setShowMoveMenu(false);
  };

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => { setHovered(false); setShowMoveMenu(false); }}
      style={{ position: 'relative', marginBottom: 'var(--space-xs)' }}
    >
      {renaming ? (
        <div style={{ padding: 'var(--space-xs) var(--space-sm)' }}>
          <input
            autoFocus
            value={renameValue}
            onChange={(e) => setRenameValue(e.target.value)}
            onBlur={handleRenameSubmit}
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleRenameSubmit();
              if (e.key === 'Escape') setRenaming(false);
            }}
            style={{ ...inlineInputStyle, width: '100%', boxSizing: 'border-box' }}
          />
        </div>
      ) : (
        <button
          onClick={() => onSelect(snippet.id)}
          style={{
            display: 'block',
            width: '100%',
            textAlign: 'left',
            padding: 'var(--space-xs) var(--space-sm)',
            paddingRight: hovered ? 72 : 'var(--space-sm)',
            background: hovered ? 'var(--color-bg-hover)' : 'transparent',
            border: 'none',
            borderRadius: 'var(--radius-md)',
            cursor: 'pointer',
          }}
        >
          <div style={{
            fontSize: 'var(--text-sm)',
            color: 'var(--color-text-primary)',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}>
            {snippet.title || '—'}
          </div>
          <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', display: 'flex', gap: 'var(--space-sm)', marginTop: 2 }}>
            <span>{formatDateShort(snippet.createdAt)}</span>
            <span style={tagStyle}>{snippet.taskKind === 'read' ? 'Read' : 'Write'}</span>
          </div>
        </button>
      )}

      {/* Action overlay */}
      {hovered && !renaming && !confirmingDelete && (
        <div style={actionOverlayStyle}>
          <button onClick={handleRenameStart} style={tinyIconBtn} title={t('renameSnippet')}>✎</button>
          {folders.length > 0 && (
            <button
              onClick={(e) => { e.stopPropagation(); setShowMoveMenu((v) => !v); }}
              style={tinyIconBtn}
              title={t('moveToFolder')}
            >
              →
            </button>
          )}
          <button
            onClick={(e) => { e.stopPropagation(); setConfirmingDelete(true); }}
            style={{ ...tinyIconBtn, color: 'var(--color-error)' }}
            title={t('deleteSnippet')}
          >✕</button>
        </div>
      )}

      {/* Move-to-folder dropdown */}
      {showMoveMenu && (
        <div style={moveMenuStyle}>
          {snippet.folderId && (
            <button onClick={(e) => handleMove(e, null)} style={moveMenuItemStyle}>
              ↑ {t('uncategorized')}
            </button>
          )}
          {folders
            .filter((f) => f.id !== snippet.folderId)
            .map((f) => (
              <button key={f.id} onClick={(e) => handleMove(e, f.id)} style={moveMenuItemStyle}>
                📁 {f.name}
              </button>
            ))}
        </div>
      )}

      {/* Inline delete confirm */}
      {confirmingDelete && (
        <div style={inlineConfirmStyle}>
          <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', flex: 1 }}>
            {t('confirmDelete')}
          </span>
          <button onClick={handleDeleteConfirm} style={{ ...tinyBtn, color: 'var(--color-error)' }}>
            {t('deleteSnippet')}
          </button>
          <button onClick={() => setConfirmingDelete(false)} style={tinyBtn}>{t('cancelRename')}</button>
        </div>
      )}
    </div>
  );
}

// --- Styles ---

const rootStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  height: '100%',
  background: 'var(--color-bg-secondary)',
  borderRight: '1px solid var(--color-border)',
};

const headerStyle: CSSProperties = {
  display: 'flex',
  justifyContent: 'space-between',
  alignItems: 'center',
  padding: 'var(--space-md) var(--space-lg)',
  borderBottom: '1px solid var(--color-border)',
};

const iconBtnStyle: CSSProperties = {
  background: 'none',
  border: 'none',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-sm)',
  padding: 'var(--space-xs)',
};

const searchInputStyle: CSSProperties = {
  width: '100%',
  padding: 'var(--space-xs) var(--space-sm)',
  background: 'var(--color-bg-primary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-primary)',
  boxSizing: 'border-box',
  outline: 'none',
};

const emptyStyle: CSSProperties = {
  padding: 'var(--space-xl)',
  textAlign: 'center',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-sm)',
};

const sectionLabelStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-muted)',
  textTransform: 'uppercase',
  letterSpacing: '0.06em',
  padding: 'var(--space-xs) var(--space-sm)',
};

const tagStyle: CSSProperties = {
  padding: '0 4px',
  borderRadius: 'var(--radius-sm)',
  background: 'var(--color-bg-tertiary)',
};

const actionOverlayStyle: CSSProperties = {
  position: 'absolute',
  right: 'var(--space-xs)',
  top: '50%',
  transform: 'translateY(-50%)',
  display: 'flex',
  gap: 2,
  background: 'var(--color-bg-secondary)',
};

const tinyIconBtn: CSSProperties = {
  background: 'none',
  border: 'none',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  padding: '2px 4px',
  borderRadius: 'var(--radius-sm)',
};

const tinyBtn: CSSProperties = {
  padding: '2px 6px',
  background: 'transparent',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--color-text-secondary)',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  whiteSpace: 'nowrap',
};

const inlineConfirmStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 'var(--space-xs)',
  padding: 'var(--space-xs) var(--space-sm)',
  borderTop: '1px solid var(--color-border)',
};

const inlineInputStyle: CSSProperties = {
  flex: 1,
  padding: '2px 6px',
  background: 'var(--color-bg-primary)',
  border: '1px solid var(--color-accent)',
  borderRadius: 'var(--radius-sm)',
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-primary)',
  outline: 'none',
};

const moveMenuStyle: CSSProperties = {
  position: 'absolute',
  right: 0,
  top: '100%',
  zIndex: 50,
  background: 'var(--color-bg-primary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  boxShadow: '0 4px 16px rgba(0,0,0,0.15)',
  minWidth: 140,
  overflow: 'hidden',
};

const moveMenuItemStyle: CSSProperties = {
  display: 'block',
  width: '100%',
  textAlign: 'left',
  padding: 'var(--space-xs) var(--space-sm)',
  background: 'transparent',
  border: 'none',
  cursor: 'pointer',
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-primary)',
};
