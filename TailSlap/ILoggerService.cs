public interface ILoggerService
{
    void Log(string message);
    void Flush();
    void Shutdown();
}
