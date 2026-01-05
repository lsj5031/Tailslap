public interface IAutoStartService
{
    bool IsEnabled(string appName);
    void Toggle(string appName);
}
