import React from 'react';
import { t } from '../i18n';

/**
 * Last-resort error boundary. Without it, ANY render throw blanks the whole
 * WebView2 panel with no recovery path except restarting Revit. The fallback
 * keeps the failure visible and offers a one-click reload of the React app.
 */
export default class ErrorBoundary extends React.Component<
  { children: React.ReactNode },
  { error: Error | null }
> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    // eslint-disable-next-line no-console
    console.error('[BIBIM] render crash:', error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div style={{
        height: '100%', display: 'flex', flexDirection: 'column',
        alignItems: 'center', justifyContent: 'center', gap: 12,
        padding: 24, textAlign: 'center',
        background: 'var(--color-bg-primary)', color: 'var(--color-text-primary)',
      }}>
        <div style={{ fontSize: 28 }}>⚠️</div>
        <div style={{ fontWeight: 600 }}>{t('errorBoundaryTitle')}</div>
        <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', maxWidth: 420 }}>
          {String(this.state.error?.message ?? this.state.error)}
        </div>
        <button
          className="btn-primary"
          onClick={() => window.location.reload()}
        >
          {t('errorBoundaryReload')}
        </button>
      </div>
    );
  }
}
