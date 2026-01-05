using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class RefinementController : IRefinementController
{
    private readonly IConfigService _config;
    private readonly IClipboardService _clip;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly IHistoryService _history;
    
    private bool _isRefining;
    private CancellationTokenSource? _currentCts;
    
    public bool IsRefining => _isRefining;
    public CancellationTokenSource? CurrentCts => _currentCts;
    
    public event Action? OnStarted;
    public event Action? OnCompleted;
    
    public RefinementController(
        IConfigService config,
        IClipboardService clip,
        ITextRefinerFactory textRefinerFactory,
        IHistoryService history)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _textRefinerFactory = textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }
    
    public async Task<bool> TriggerRefineAsync()
    {
        var cfg = _config.CreateValidatedCopy();
        
        if (!cfg.Llm.Enabled)
        {
            NotificationService.ShowWarning("LLM processing is disabled. Enable it in settings first.");
            return false;
        }
        
        if (_isRefining)
        {
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                CancelRefine();
                return false;
            }
            NotificationService.ShowWarning("Refinement already in progress. Please wait.");
            return false;
        }
        
        _isRefining = true;
        _currentCts = new CancellationTokenSource();
        OnStarted?.Invoke();
        
        try
        {
            await RefineSelectionAsync(cfg, _currentCts.Token);
            return true;
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
            _isRefining = false;
            OnCompleted?.Invoke();
        }
    }
    
    public void CancelRefine()
    {
        try
        {
            _currentCts?.Cancel();
            NotificationService.ShowInfo("Refinement cancelled.");
            Logger.Log("Refinement cancelled by user.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error cancelling refinement: {ex.Message}");
        }
    }
    
    private async Task RefineSelectionAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            Logger.Log("RefineSelectionAsync started");
            Logger.Log("Starting capture from selection/clipboard");
            
            var text = await _clip.CaptureSelectionOrClipboardAsync(cfg.UseClipboardFallback);
            Logger.Log($"Captured length: {text?.Length ?? 0}, sha256={Sha256Hex(text ?? string.Empty)}");
            
            if (string.IsNullOrWhiteSpace(text))
            {
                NotificationService.ShowWarning("No text selected or in clipboard.");
                return;
            }
            
            ct.ThrowIfCancellationRequested();
            
            var refiner = _textRefinerFactory.Create(cfg.Llm);
            var refined = await refiner.RefineAsync(text, ct);
            Logger.Log($"Refined length: {refined?.Length ?? 0}, sha256={Sha256Hex(refined ?? string.Empty)}");
            
            if (string.IsNullOrWhiteSpace(refined))
            {
                NotificationService.ShowError("Provider returned empty result.");
                return;
            }
            
            ct.ThrowIfCancellationRequested();
            
            bool setTextSuccess = _clip.SetText(refined);
            if (!setTextSuccess)
            {
                return;
            }
            
            await Task.Delay(100, ct);
            
            if (cfg.AutoPaste)
            {
                Logger.Log("Auto-paste attempt");
                bool pasteSuccess = await _clip.PasteAsync();
                if (!pasteSuccess)
                {
                    NotificationService.ShowInfo("Text is ready. You can paste manually with Ctrl+V.");
                }
            }
            else
            {
                NotificationService.ShowTextReadyNotification();
            }
            
            try
            {
                _history.Append(text, refined, cfg.Llm.Model);
            }
            catch { }
            
            Logger.Log("Refinement completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Refinement was cancelled.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError("Refinement failed: " + ex.Message);
            Logger.Log("Error: " + ex.Message);
        }
    }
    
    private static string Sha256Hex(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        try
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(s);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(inputBytes, hash);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }
}
