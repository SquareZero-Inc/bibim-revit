import React, { useState, type CSSProperties } from 'react';
import { formatDateShort, t } from '../i18n';
import type { SessionInfo } from '../types';

interface Props {
  sessions: SessionInfo[];
  activeId: string | null;
  pendingLoadSessionId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  onClose: () => void;
  onDelete: (id: string) => void;
  onRename: (id: string, newTitle: string) => void;
  onConfirmLoad: () => void;
  onCancelLoad: () => void;
}

export default function HistoryPanel({
  sessions,
  activeId,
  pendingLoadSessionId,
  onSelect,
  onNew,
  onClose,
  onDelete,
  onRename,
  onConfirmLoad,
  onCancelLoad,
}: Props) {
  const [searchQuery, setSearchQuery] = useState('');

  const rootSessions = sessions.filter((s) => !s.parentSessionId);
  const filtered = searchQuery.trim()
    ? rootSessions.filter((s) =>
        (s.title || '').toLowerCase().includes(searchQuery.toLowerCase())
      )
    : rootSessions;

  return (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      height: '100%',
      background: 'var(--color-bg-secondary)',
      borderRight: '1px solid var(--color-border)',
    }}>
      {/* Header */}
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        padding: 'var(--space-md) var(--space-lg)',
        borderBottom: '1px solid var(--color-border)',
      }}>
        <span style={{ fontSize: 'var(--text-sm)', fontWeight: 600 }}>{t('historyTitle')}</span>
        <div style={{ display: 'flex', gap: 'var(--space-sm)' }}>
          <button onClick={onNew} style={iconBtnStyle} title={t('newSession')}>+</button>
          <button onClick={onClose} style={iconBtnStyle} title={t('close')}>X</button>
        </div>
      </div>

      {/* Search */}
      <div style={{ padding: 'var(--space-sm) var(--space-md)' }}>
        <input
          type="text"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder={t('searchSessions')}
          style={searchInputStyle}
        />
      </div>

      {/* Load confirmation banner */}
      {pendingLoadSessionId && (
        <div style={confirmBannerStyle}>
          <span style={{ fontSize: 'var(--text-xs)', flex: 1 }}>{t('confirmLoadSession')}</span>
          <button onClick={onConfirmLoad} style={confirmBtnStyle}>{t('loadSessionConfirm')}</button>
          <button onClick={onCancelLoad} style={cancelBtnStyle}>{t('cancelRename')}</button>
        </div>
      )}

      {/* Session list */}
      <div style={{ flex: 1, overflow: 'auto', padding: 'var(--space-sm)' }}>
        {filtered.length === 0 && (
          <div style={{
            padding: 'var(--space-xl)',
            textAlign: 'center',
            color: 'var(--color-text-muted)',
            fontSize: 'var(--text-sm)',
          }}>
            {sessions.length === 0 ? t('noSessions') : '—'}
          </div>
        )}

        {filtered.map((session) => (
          <div key={session.id}>
            <SessionItem
              session={session}
              isActive={session.id === activeId}
              isPendingLoad={session.id === pendingLoadSessionId}
              onSelect={onSelect}
              onDelete={onDelete}
              onRename={onRename}
            />
            {sessions
              .filter((child) => child.parentSessionId === session.id)
              .map((child) => (
                <SessionItem
                  key={child.id}
                  session={child}
                  isActive={child.id === activeId}
                  isPendingLoad={child.id === pendingLoadSessionId}
                  onSelect={onSelect}
                  onDelete={onDelete}
                  onRename={onRename}
                  indent
                />
              ))}
          </div>
        ))}
      </div>
    </div>
  );
}

interface SessionItemProps {
  session: SessionInfo;
  isActive: boolean;
  isPendingLoad: boolean;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
  onRename: (id: string, newTitle: string) => void;
  indent?: boolean;
}

function SessionItem({
  session,
  isActive,
  isPendingLoad,
  onSelect,
  onDelete,
  onRename,
  indent = false,
}: SessionItemProps) {
  const [hovered, setHovered] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const handleRenameStart = (e: React.MouseEvent) => {
    e.stopPropagation();
    setRenameValue(session.title || '');
    setRenaming(true);
    setConfirmingDelete(false);
  };

  const handleRenameSubmit = (e: React.MouseEvent | React.KeyboardEvent) => {
    e.stopPropagation();
    const trimmed = renameValue.trim();
    if (trimmed) onRename(session.id, trimmed);
    setRenaming(false);
  };

  const handleRenameCancel = (e: React.MouseEvent) => {
    e.stopPropagation();
    setRenaming(false);
  };

  const handleDeleteRequest = (e: React.MouseEvent) => {
    e.stopPropagation();
    setConfirmingDelete(true);
    setRenaming(false);
  };

  const handleDeleteConfirm = (e: React.MouseEvent) => {
    e.stopPropagation();
    onDelete(session.id);
    setConfirmingDelete(false);
  };

  const handleDeleteCancel = (e: React.MouseEvent) => {
    e.stopPropagation();
    setConfirmingDelete(false);
  };

  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        position: 'relative',
        borderRadius: 'var(--radius-md)',
        marginBottom: 'var(--space-xs)',
        background: isPendingLoad
          ? 'var(--color-accent-muted, rgba(59,130,246,0.1))'
          : isActive
          ? 'var(--color-bg-hover)'
          : 'transparent',
        border: isPendingLoad ? '1px solid var(--color-accent)' : '1px solid transparent',
      }}
    >
      <button
        onClick={() => onSelect(session.id)}
        style={{
          display: 'block',
          width: '100%',
          textAlign: 'left',
          padding: 'var(--space-sm) var(--space-md)',
          paddingLeft: indent ? 'calc(var(--space-md) + 20px)' : 'var(--space-md)',
          paddingRight: hovered ? '60px' : 'var(--space-md)',
          background: 'transparent',
          border: 'none',
          cursor: 'pointer',
        }}
      >
        {indent && (
          <span style={{
            position: 'absolute',
            left: 'var(--space-sm)',
            top: 'var(--space-sm)',
            fontSize: 'var(--text-xs)',
            color: 'var(--color-text-muted)',
          }}>
            +-
          </span>
        )}

        {renaming ? (
          <div onClick={(e) => e.stopPropagation()} style={{ display: 'flex', gap: 4 }}>
            <input
              autoFocus
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') handleRenameSubmit(e);
                if (e.key === 'Escape') setRenaming(false);
              }}
              style={renameInputStyle}
            />
            <button onClick={handleRenameSubmit} style={tinyBtnStyle}>{t('saveRename')}</button>
            <button onClick={handleRenameCancel} style={{ ...tinyBtnStyle, color: 'var(--color-text-muted)' }}>{t('cancelRename')}</button>
          </div>
        ) : (
          <div style={{
            fontSize: 'var(--text-sm)',
            color: 'var(--color-text-primary)',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}>
            {session.title || t('untitled')}
          </div>
        )}

        {!renaming && (
          <div style={{
            fontSize: 'var(--text-xs)',
            color: 'var(--color-text-muted)',
            display: 'flex',
            justifyContent: 'space-between',
            marginTop: 'var(--space-xs)',
          }}>
            <span>{formatDateShort(session.createdAt)}</span>
            <span>{session.messageCount} {t('messages')}</span>
          </div>
        )}
      </button>

      {/* Action buttons overlay */}
      {hovered && !renaming && !confirmingDelete && (
        <div style={actionOverlayStyle}>
          <button onClick={handleRenameStart} style={smallIconBtnStyle} title={t('renameSession')}>✎</button>
          <button onClick={handleDeleteRequest} style={{ ...smallIconBtnStyle, color: 'var(--color-error)' }} title={t('deleteSession')}>✕</button>
        </div>
      )}

      {/* Inline delete confirm */}
      {confirmingDelete && (
        <div style={deleteConfirmStyle}>
          <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', flex: 1 }}>
            {t('confirmDeleteSession')}
          </span>
          <button onClick={handleDeleteConfirm} style={{ ...tinyBtnStyle, color: 'var(--color-error)' }}>
            {t('deleteSession')}
          </button>
          <button onClick={handleDeleteCancel} style={tinyBtnStyle}>{t('cancelRename')}</button>
        </div>
      )}
    </div>
  );
}

const iconBtnStyle: CSSProperties = {
  background: 'none',
  border: 'none',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-base)',
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

const confirmBannerStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 'var(--space-xs)',
  padding: 'var(--space-xs) var(--space-md)',
  background: 'var(--color-accent-muted, rgba(59,130,246,0.12))',
  borderBottom: '1px solid var(--color-border)',
};

const confirmBtnStyle: CSSProperties = {
  padding: '2px 8px',
  background: 'var(--color-accent)',
  border: 'none',
  borderRadius: 'var(--radius-sm)',
  color: '#fff',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  fontWeight: 600,
  whiteSpace: 'nowrap',
};

const cancelBtnStyle: CSSProperties = {
  padding: '2px 6px',
  background: 'transparent',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  whiteSpace: 'nowrap',
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

const smallIconBtnStyle: CSSProperties = {
  background: 'none',
  border: 'none',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  padding: '2px 4px',
  borderRadius: 'var(--radius-sm)',
};

const renameInputStyle: CSSProperties = {
  flex: 1,
  padding: '2px 6px',
  background: 'var(--color-bg-primary)',
  border: '1px solid var(--color-accent)',
  borderRadius: 'var(--radius-sm)',
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-primary)',
  outline: 'none',
};

const tinyBtnStyle: CSSProperties = {
  padding: '2px 6px',
  background: 'transparent',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--color-text-secondary)',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  whiteSpace: 'nowrap',
};

const deleteConfirmStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 'var(--space-xs)',
  padding: 'var(--space-xs) var(--space-md)',
  borderTop: '1px solid var(--color-border)',
};
