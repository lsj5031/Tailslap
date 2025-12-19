using System.Threading.Tasks;

public interface IClipboardService
{
    Task<string> CaptureSelectionOrClipboardAsync(bool useClipboardFallback = false);
    bool SetText(string text);
    Task<bool> PasteAsync();
}
