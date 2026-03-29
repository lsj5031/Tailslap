using System;
using System.Threading;
using System.Threading.Tasks;
using TailSlap;

public sealed class RefinementController : IRefinementController
{
    private readonly IConfigService _config;
    private readonly IClipboardService _clip;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly IHistoryService _history;
    private readonly ClipboardHelper _clipboardHelper;

    private bool _isRefining;
    private CancellationTokenSource? _currentCts;
    private readonly object _ctsLock = new();

    public bool IsRefining => _isRefining;
    public CancellationTokenSource? CurrentCts => _currentCts;

    public event Action? OnStarted;
    public event Action? OnCompleted;

    public RefinementController(
        IConfigService config,
        IClipboardService clip,
        ITextRefinerFactory textRefinerFactory,
        IHistoryService history,
        ClipboardHelper clipboardHelper
    )
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _textRefinerFactory =
            textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _clipboardHelper =
            clipboardHelper ?? throw new ArgumentNullException(nameof(clipboardHelper));
    }

    public async Task<bool> TriggerRefineAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        if (!cfg.Llm.Enabled)
        {
            NotificationService.ShowWarning(
                "LLM processing is disabled. Enable it in settings first."
            );
            return false;
        }

        if (_isRefining)
        {
            CancellationTokenSource? ctsToCheck;
            lock (_ctsLock)
            {
                ctsToCheck = _currentCts;
            }
            if (ctsToCheck != null && !ctsToCheck.IsCancellationRequested)
            {
                CancelRefine();
                return false;
            }
            NotificationService.ShowWarning("Refinement already in progress. Please wait.");
            return false;
        }

        _isRefining = true;
        lock (_ctsLock)
        {
            _currentCts = new CancellationTokenSource();
        }
        OnStarted?.Invoke();

        try
        {
            CancellationToken token;
            lock (_ctsLock)
            {
                token = _currentCts?.Token ?? CancellationToken.None;
            }
            var success = await RefineSelectionAsync(cfg, token);
            return success;
        }
        finally
        {
            lock (_ctsLock)
            {
                _currentCts?.Dispose();
                _currentCts = null;
            }
            _isRefining = false;
            OnCompleted?.Invoke();
        }
    }

    public void CancelRefine()
    {
        try
        {
            CancellationTokenSource? cts;
            lock (_ctsLock)
            {
                cts = _currentCts;
            }
            cts?.Cancel();
            NotificationService.ShowInfo("Refinement cancelled.");
            Logger.Log("Refinement cancelled by user.");
        }
        catch (Exception ex)
        {
            Logger.Log("Error cancelling refinement: " + ex.Message);
        }
    }

    private async Task<bool> RefineSelectionAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            Logger.Log("RefineSelectionAsync started");
            Logger.Log("Starting capture from selection/clipboard");

            var text = await _clip.CaptureSelectionOrClipboardAsync(cfg.UseClipboardFallback);
            Logger.Log(
                $"Captured length: {text?.Length ?? 0}, sha256={Hashing.Sha256Hex(text ?? string.Empty)}"
            );

            if (string.IsNullOrWhiteSpace(text))
            {
                NotificationService.ShowWarning("No text selected or in clipboard.");
                return false;
            }

            ct.ThrowIfCancellationRequested();

            var refiner = _textRefinerFactory.Create(cfg.Llm);
            var refined = await refiner.RefineAsync(text, ct);
            Logger.Log(
                $"Refined length: {refined?.Length ?? 0}, sha256={Hashing.Sha256Hex(refined ?? string.Empty)}"
            );

            if (string.IsNullOrWhiteSpace(refined))
            {
                NotificationService.ShowError("Provider returned empty result.");
                return false;
            }

            ct.ThrowIfCancellationRequested();

            var success = await _clipboardHelper.SetTextAndPasteAsync(refined, cfg.AutoPaste);

            try
            {
                _history.Append(text, refined, cfg.Llm.Model);
            }
            catch { }

            Logger.Log("Refinement completed successfully.");
            return success;
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Refinement was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            NotificationService.ShowError("Refinement failed: " + ex.Message);
            Logger.Log("Error: " + ex.Message);
            return false;
        }
    }
}
