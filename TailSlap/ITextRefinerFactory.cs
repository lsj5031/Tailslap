public interface ITextRefinerFactory
{
    ITextRefiner Create(LlmConfig cfg);
}
