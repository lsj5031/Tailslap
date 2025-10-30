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

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
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
    }

    public static void ShowInfo(string message, string title = "TailSlap")
    {
        EnqueueNotification(title, message, NotificationType.Info, 3000);
    }

    public static void ShowSuccess(string message, string title = "TailSlap")
    {
        EnqueueNotification(title, message, NotificationType.Success, 2000);
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
            MessageBoxIcon.Warning);
        
        if (result == DialogResult.OK)
        {
            ShowInfo("You can now paste with Ctrl+V");
        }
    }

    private static void EnqueueNotification(string title, string message, NotificationType type, int durationMs)
    {
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
            DurationMs = durationMs
        };

        lock (_lockObject)
        {
            _messageQueue.Enqueue(notification);
        }

        // Start processing if not already running
        if (!_isProcessing)
        {
            _ = Task.Run(ProcessQueueAsync);
        }
    }

    private static async Task ProcessQueueAsync()
    {
        _isProcessing = true;

        try
        {
            while (true)
            {
                NotificationMessage notification;
                
                lock (_lockObject)
                {
                    if (_messageQueue.Count == 0)
                    {
                        _isProcessing = false;
                        return;
                    }
                    
                    notification = _messageQueue.Dequeue();
                }

                // Show the notification on the UI thread
                _ = Task.Run(() => ShowNotificationOnUiThread(notification));
                
                // Wait before showing the next notification
                await Task.Delay(notification.DurationMs + 500);
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"NotificationService error: {ex.Message}"); } catch { }
            _isProcessing = false;
        }
    }

    private static void ShowNotificationOnUiThread(NotificationMessage notification)
    {
        try
        {
            // Since NotifyIcon doesn't have InvokeRequired, we'll use a different approach
            // We'll show the notification directly as it's already on the UI thread in most cases

            var icon = notification.Type switch
            {
                NotificationType.Success => ToolTipIcon.Info,
                NotificationType.Warning => ToolTipIcon.Warning,
                NotificationType.Error => ToolTipIcon.Error,
                _ => ToolTipIcon.Info
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
                    icon);
            }
            catch (Exception ex)
            {
                try { Logger.Log($"Balloon tip failed: {ex.Message}"); } catch { }
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
            try { Logger.Log($"Failed to show notification: {ex.Message}"); } catch { }
            try { ShowFallbackMessageBox(notification.Title, notification.Message, notification.Type); } catch { }
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
                _ => MessageBoxIcon.Information
            };

            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }
        catch { }
    }
}
