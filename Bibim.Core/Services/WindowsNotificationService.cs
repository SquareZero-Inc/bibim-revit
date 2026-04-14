// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Bibim.Core
{
    /// <summary>
    /// Windows balloon-tip notification service.
    /// Shows a system tray notification when BIBIM needs user attention.
    /// </summary>
    public static class WindowsNotificationService
    {
        private static NotifyIcon _notifyIcon;
        private static readonly object _lock = new object();

        /// <summary>
        /// Show a balloon notification. Creates the NotifyIcon lazily on first call.
        /// </summary>
        public static void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                lock (_lock)
                {
                    if (_notifyIcon == null)
                    {
                        _notifyIcon = new NotifyIcon();
                        _notifyIcon.Icon = LoadAppIcon();
                        _notifyIcon.Text = "BIBIM AI";
                    }

                    _notifyIcon.Visible = true;
                    _notifyIcon.ShowBalloonTip(3000, title, message, icon);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("WindowsNotification", $"Failed to show notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Show the standard "action required" notification in the user's language.
        /// </summary>
        public static void NotifyActionRequired()
        {
            string title = "BIBIM AI";
            string message = AppLanguage.IsEnglish
                ? "Action required — please check BIBIM."
                : "다음 액션이 필요합니다 — BIBIM을 확인해주세요.";

            ShowNotification(title, message);
        }

        /// <summary>
        /// Clean up the NotifyIcon on shutdown.
        /// </summary>
        public static void Dispose()
        {
            lock (_lock)
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? "";
                string icoPath = Path.Combine(assemblyDir, "Assets", "Icons", "bibim-icon-blue.ico");

                if (File.Exists(icoPath))
                    return new Icon(icoPath);
            }
            catch { /* fall through to default */ }

            return SystemIcons.Information;
        }
    }
}
