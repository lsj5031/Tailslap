using System.Threading.Tasks;

public interface IClipboardService
{
    Task<string> CaptureSelectionOrClipboardAsync(bool useClipboardFallback = false);
    Task<bool> SetTextAsync(string text);
    Task<bool> PasteAsync();
    Task<bool> SetTextAndPasteAsync(string text);
}
