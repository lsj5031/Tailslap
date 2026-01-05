using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class NotificationService
{
    private static readonly Queue<NotificationMessage> _messageQueue = new();
    private static readonly object _lockObject = new();
    private static bool _isProcessing = false;
    private static NotifyIcon? _trayIcon;
    private static SynchronizationContext? _uiContext;

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error,
    }

    private sealed class NotificationMessage
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public NotificationType Type { get; set; }
        public int DurationMs { get; set; }
    }

    public static void Initialize(NotifyIcon trayIcon)
    {
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _uiContext = SynchronizationContext.Current;
    }

    public static void ShowInfo(string message, string title = "TailSlap", int durationMs = 3000)
    {
        try
        {
            Logger.Log($"[Info]: {message}");
            EnqueueNotification(title, message, NotificationType.Info, durationMs);
        }
        catch { }
    }

    public static void ShowSuccess(string message, string title = "TailSlap")
    {
        try
        {
            Logger.Log($"[Success]: {message}");
            EnqueueNotification(title, message, NotificationType.Success, 3000);
        }
        catch { }
    }

    public static void ShowWarning(string message, string title = "TailSlap")
    {
        EnqueueNotification(title, message, NotificationType.Warning, 4000);
    }

    public static void ShowError(string message, string title = "TailSlap")
    {
        EnqueueNotification(title, message, NotificationType.Error, 5000);
    }

    public static void ShowTextReadyNotification()
    {
        ShowSuccess("Text refined and ready to paste!");
    }

    public static void ShowAutoPasteFailedNotification()
    {
        var result = MessageBox.Show(
            "Auto-paste failed. Please paste manually (Ctrl+V).",
            "Paste Failed",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning
        );

        if (result == DialogResult.OK)
        {
            ShowInfo("You can now paste with Ctrl+V");
        }
    }

    private static void EnqueueNotification(
        string title,
        string message,
        NotificationType type,
        int durationMs
    )
    {
        // Only show warnings and errors in the notification area to reduce noise
        if (type == NotificationType.Info || type == NotificationType.Success)
        {
            return;
        }

        if (_trayIcon == null)
        {
            // Fallback to simple message box if no tray icon
            ShowFallbackMessageBox(title, message, type);
            return;
        }

        var notification = new NotificationMessage
        {
            Title = title,
            Message = message,
            Type = type,
            DurationMs = durationMs,
        };

        lock (_lockObject)
        {
            _messageQueue.Enqueue(notification);

            // Start processing if not already running (inside the lock to prevent race)
            if (!_isProcessing)
            {
                _isProcessing = true;
                _ = ProcessQueueAsync();
            }
        }
    }

    private static async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                NotificationMessage? notification = null;

                lock (_lockObject)
                {
                    if (_messageQueue.Count == 0)
                    {
                        _isProcessing = false;
                        return;
                    }

                    notification = _messageQueue.Dequeue();
                }

                if (notification != null)
                {
                    // Marshal notification display back to UI thread if possible
                    if (_uiContext != null)
                    {
                        _uiContext.Post(_ => ShowNotificationOnUiThread(notification), null);
                    }
                    else
                    {
                        ShowNotificationOnUiThread(notification);
                    }

                    // Wait before showing the next notification
                    await Task.Delay(notification.DurationMs + 500).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"NotificationService error: {ex.Message}");
            }
            catch { }
            lock (_lockObject)
            {
                _isProcessing = false;
            }
        }
    }

    private static void ShowNotificationOnUiThread(NotificationMessage notification)
    {
        try
        {
            var icon = notification.Type switch
            {
                NotificationType.Success => ToolTipIcon.Info,
                NotificationType.Warning => ToolTipIcon.Warning,
                NotificationType.Error => ToolTipIcon.Error,
                _ => ToolTipIcon.Info,
            };

            // Add additional safety check for tray icon
            if (_trayIcon == null)
            {
                ShowFallbackMessageBox(notification.Title, notification.Message, notification.Type);
                return;
            }

            try
            {
                _trayIcon.ShowBalloonTip(
                    notification.DurationMs / 1000,
                    notification.Title,
                    notification.Message,
                    icon
                );
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"Balloon tip failed: {ex.Message}");
                }
                catch { }
                // Fallback to message box if balloon tip fails
                ShowFallbackMessageBox(notification.Title, notification.Message, notification.Type);
            }

            // Also log the notification
            try
            {
                Logger.Log($"Notification [{notification.Type}]: {notification.Message}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Failed to show notification: {ex.Message}");
            }
            catch { }
            try
            {
                ShowFallbackMessageBox(notification.Title, notification.Message, notification.Type);
            }
            catch { }
        }
    }

    private static void ShowFallbackMessageBox(string title, string message, NotificationType type)
    {
        try
        {
            var icon = type switch
            {
                NotificationType.Error => MessageBoxIcon.Error,
                NotificationType.Warning => MessageBoxIcon.Warning,
                NotificationType.Success => MessageBoxIcon.Information,
                _ => MessageBoxIcon.Information,
            };

            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }
        catch { }
    }
}

public sealed class NotificationServiceAdapter : INotificationService
{
    public void ShowInfo(string message, string title = "TailSlap", int durationMs = 3000)
        => NotificationService.ShowInfo(message, title, durationMs);

    public void ShowSuccess(string message, string title = "TailSlap")
        => NotificationService.ShowSuccess(message, title);

    public void ShowWarning(string message, string title = "TailSlap")
        => NotificationService.ShowWarning(message, title);

    public void ShowError(string message, string title = "TailSlap")
        => NotificationService.ShowError(message, title);

    public void ShowTextReadyNotification()
        => NotificationService.ShowTextReadyNotification();

    public void ShowAutoPasteFailedNotification()
        => NotificationService.ShowAutoPasteFailedNotification();
}
