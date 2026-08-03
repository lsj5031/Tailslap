using System.Threading.Tasks;

namespace TailSlap;

public sealed class ClipboardHelper
{
    private readonly IClipboardService _clip;

    public ClipboardHelper(IClipboardService clip)
    {
        _clip = clip ?? throw new System.ArgumentNullException(nameof(clip));
    }

    public async Task<bool> SetTextAndPasteAsync(
        string text,
        bool autoPaste,
        System.IntPtr? expectedForegroundWindow = null
    )
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        bool setTextSuccess = await _clip.SetTextAsync(text).ConfigureAwait(false);
        if (!setTextSuccess)
        {
            Logger.LogWarning("ClipboardHelper: SetTextAsync failed, text could not be delivered");
            NotificationService.ShowError("Failed to set clipboard text. Please try again.");
            return false;
        }

        await Task.Delay(100).ConfigureAwait(false);

        if (autoPaste)
        {
            Logger.Log("Auto-paste attempt");
            bool pasteSuccess = await _clip
                .PasteAsync(expectedForegroundWindow)
                .ConfigureAwait(false);
            if (!pasteSuccess)
            {
                Logger.LogWarning("ClipboardHelper: Auto-paste failed, text is on the clipboard");
                NotificationService.ShowWarning(
                    "Auto-paste failed. The text is on your clipboard — paste manually with Ctrl+V."
                );
            }
            return pasteSuccess;
        }
        else
        {
            NotificationService.ShowTextReadyNotification();
            return true;
        }
    }
}
