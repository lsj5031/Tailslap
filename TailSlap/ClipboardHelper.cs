using System.Threading.Tasks;

namespace TailSlap;

public sealed class ClipboardHelper
{
    private readonly IClipboardService _clip;

    public ClipboardHelper(IClipboardService clip)
    {
        _clip = clip ?? throw new System.ArgumentNullException(nameof(clip));
    }

    public async Task<bool> SetTextAndPasteAsync(string text, bool autoPaste)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        bool setTextSuccess = await _clip.SetTextAsync(text).ConfigureAwait(false);
        if (!setTextSuccess)
        {
            return false;
        }

        await Task.Delay(100).ConfigureAwait(false);

        if (autoPaste)
        {
            Logger.Log("Auto-paste attempt");
            bool pasteSuccess = await _clip.PasteAsync().ConfigureAwait(false);
            if (!pasteSuccess)
            {
                NotificationService.ShowInfo("Text is ready. You can paste manually with Ctrl+V.");
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
