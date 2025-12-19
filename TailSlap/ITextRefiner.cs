using System.Threading;
using System.Threading.Tasks;

public interface ITextRefiner
{
    Task<string> RefineAsync(string text, CancellationToken ct = default);
}
