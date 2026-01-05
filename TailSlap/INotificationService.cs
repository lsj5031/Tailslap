public interface INotificationService
{
    void ShowInfo(string message, string title = "TailSlap", int durationMs = 3000);
    void ShowSuccess(string message, string title = "TailSlap");
    void ShowWarning(string message, string title = "TailSlap");
    void ShowError(string message, string title = "TailSlap");
    void ShowTextReadyNotification();
    void ShowAutoPasteFailedNotification();
}
