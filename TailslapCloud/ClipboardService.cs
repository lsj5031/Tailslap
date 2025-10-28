using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public sealed class ClipboardService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public string CaptureSelectionOrClipboard()
    {
        string? originalClipboard = null;
        try 
        { 
            if (Clipboard.ContainsText()) 
                originalClipboard = Clipboard.GetText(TextDataFormat.UnicodeText); 
        } 
        catch { }
        
        IntPtr foregroundWindow = GetForegroundWindow();
        
        try 
        { 
            Clipboard.Clear();
            Thread.Sleep(100);
        } 
        catch { }
        
        try 
        {
            if (foregroundWindow != IntPtr.Zero)
            {
                SetForegroundWindow(foregroundWindow);
                Thread.Sleep(100);
            }
            
            SendKeys.SendWait("^c");
            Thread.Sleep(500);
        } 
        catch (Exception ex) 
        { 
            try { Logger.Log($"SendKeys error: {ex.Message}"); } catch { }
        }
        
        try 
        { 
            if (Clipboard.ContainsText()) 
            {
                var newText = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(newText))
                {
                    try { Logger.Log($"Captured new text: {newText.Length} chars"); } catch { }
                    return newText;
                }
            }
        } 
        catch { }
        
        try
        {
            if (!string.IsNullOrWhiteSpace(originalClipboard))
            {
                Clipboard.SetText(originalClipboard, TextDataFormat.UnicodeText);
            }
        }
        catch { }
        
        try { Logger.Log($"Returning original clipboard: {originalClipboard?.Length ?? 0} chars"); } catch { }
        return originalClipboard ?? string.Empty;
    }

    public void SetText(string text)
    {
        int retries = 3;
        while (retries-- > 0)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); return; }
            catch { Thread.Sleep(50); }
        }
    }

    public void Paste() 
    { 
        try 
        { 
            Thread.Sleep(100);
            SendKeys.SendWait("+{INSERT}");
            Thread.Sleep(50);
        } 
        catch { } 
    }
}
