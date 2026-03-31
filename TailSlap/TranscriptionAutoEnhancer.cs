using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

internal static class TranscriptionAutoEnhancer
{
    public static async Task<string> MaybeEnhanceAsync(
        string transcriptionText,
        AppConfig cfg,
        ITextRefinerFactory textRefinerFactory,
        CancellationToken ct = default
    )
    {
        if (!cfg.Transcriber.EnableAutoEnhance)
            return transcriptionText;

        if (transcriptionText.Length < cfg.Transcriber.AutoEnhanceThresholdChars)
            return transcriptionText;

        if (!cfg.Llm.Enabled)
        {
            Logger.Log("Auto-enhancement skipped: LLM is disabled");
            return transcriptionText;
        }

        try
        {
            Logger.Log(
                $"Auto-enhancing transcription ({transcriptionText.Length} chars >= {cfg.Transcriber.AutoEnhanceThresholdChars} threshold)"
            );
            NotificationService.ShowInfo("Enhancing transcription with LLM...");

            var enhancementConfig = cfg.Llm.Clone();
            enhancementConfig.Temperature = Math.Min(enhancementConfig.Temperature, 0.2);
            var refiner = textRefinerFactory.Create(enhancementConfig);
            var enhanced = await refiner.RefineAsync(transcriptionText, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(enhanced) && enhanced.Length > 0)
            {
                if (!ShouldUseEnhancedText(transcriptionText, enhanced, out var rejectionReason))
                {
                    Logger.Log(
                        $"Auto-enhancement rejected: {rejectionReason}. Keeping original transcription."
                    );
                    NotificationService.ShowWarning(
                        "Enhancement looked unreliable. Using the original transcription."
                    );
                    return transcriptionText;
                }

                Logger.Log(
                    $"Transcription enhanced: {transcriptionText.Length} -> {enhanced.Length} chars"
                );
                NotificationService.ShowSuccess("Transcription enhanced!");
                return enhanced;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Auto-enhancement failed: {ex.Message}. Using original transcription.");
            NotificationService.ShowWarning("Enhancement failed. Using original transcription.");
        }

        return transcriptionText;
    }

    internal static bool ShouldUseEnhancedText(
        string original,
        string enhanced,
        out string rejectionReason
    )
    {
        var originalTrimmed = original?.Trim() ?? string.Empty;
        var enhancedTrimmed = enhanced?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enhancedTrimmed))
        {
            rejectionReason = "empty enhancement";
            return false;
        }

        if (string.Equals(originalTrimmed, enhancedTrimmed, StringComparison.Ordinal))
        {
            rejectionReason = string.Empty;
            return true;
        }

        if (
            originalTrimmed.Length >= 80
            && enhancedTrimmed.Length < Math.Max(20, originalTrimmed.Length / 2)
        )
        {
            rejectionReason =
                $"enhancement shrank too far ({originalTrimmed.Length} -> {enhancedTrimmed.Length})";
            return false;
        }

        var originalWords = SplitWords(originalTrimmed);
        var enhancedWords = SplitWords(enhancedTrimmed);
        if (originalWords.Length >= 6 && enhancedWords.Length > 0)
        {
            int sharedWords = enhancedWords.Count(word => originalWords.Contains(word));
            double overlap = sharedWords / (double)enhancedWords.Length;
            if (overlap < 0.35)
            {
                rejectionReason = $"lexical overlap too low ({overlap:F2})";
                return false;
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static string[] SplitWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(
                new[]
                {
                    ' ',
                    '\t',
                    '\r',
                    '\n',
                    '.',
                    ',',
                    ';',
                    ':',
                    '!',
                    '?',
                    '(',
                    ')',
                    '[',
                    ']',
                    '{',
                    '}',
                    '"',
                    '\'',
                    '-',
                    '_',
                    '/',
                },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
    }
}
