using System;
using System.Runtime.CompilerServices;

public interface ILoggerService
{
    void Log(string message, [CallerMemberName] string source = "");
    void LogWarning(string message, [CallerMemberName] string source = "");
    void Error(string message, Exception? ex = null, [CallerMemberName] string source = "");
    void Debug(string message, [CallerMemberName] string source = "");
    void LogVerbose(string message, [CallerMemberName] string source = "");
    void Flush();
    void Shutdown();
}
