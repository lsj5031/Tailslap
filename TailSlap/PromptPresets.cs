using System.Collections.Generic;

namespace TailSlap;

/// <summary>
/// Built-in refinement prompt presets for the Settings UI (option A: no schema change).
/// </summary>
public static class PromptPresets
{
    public sealed record Preset(string Name, string Body);

    public static IReadOnlyList<Preset> All { get; } =
        new[]
        {
            new Preset("Dictation polish (default)", LlmConfig.DefaultRefinementPrompt),
            new Preset(
                "Concise email",
                """
                You turn rough dictated text into a concise professional email body.

                Preserve meaning and facts. Remove filler and speech artifacts.
                Use clear paragraphs. Prefer short sentences.
                Do not add a subject line, greeting, or sign-off unless the input already includes them.
                Return only the polished email body.
                """
            ),
            new Preset(
                "Preserve technical terms",
                """
                You polish dictated technical text for engineers.

                Preserve identifiers, API names, file paths, commands, and domain jargon exactly when present.
                Fix grammar and dictation artifacts without inventing new technical claims.
                Keep structure (lists, code-like tokens) when useful.
                Return only the polished text.
                """
            ),
        };
}
