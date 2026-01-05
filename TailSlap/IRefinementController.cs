using System;
using System.Threading;
using System.Threading.Tasks;

public interface IRefinementController
{
    bool IsRefining { get; }
    CancellationTokenSource? CurrentCts { get; }
    
    Task<bool> TriggerRefineAsync();
    void CancelRefine();
    
    event Action? OnStarted;
    event Action? OnCompleted;
}
