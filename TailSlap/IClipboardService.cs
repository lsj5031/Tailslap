using System.Threading.Tasks;

public interface IClipboardService
{
    Task<string> CaptureSelectionOrClipboardAsync(
        bool useClipboardFallback = false,
        System.IntPtr? targetWindow = null
    );
    Task<bool> SetTextAsync(string text);
    Task<bool> PasteAsync(
        System.IntPtr? expectedForegroundWindow = null,
        uint expectedProcessId = 0,
        string? expectedWindowClass = null
    );
    Task<bool> SetTextAndPasteAsync(string text);
}
