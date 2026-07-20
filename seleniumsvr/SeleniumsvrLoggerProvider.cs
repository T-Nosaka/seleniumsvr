using Microsoft.Extensions.Logging;

namespace seleniumsvr;

/// <summary>
/// ログアダプター。
/// </summary>
internal sealed class SeleniumsvrLoggerAdapter : Microsoft.Extensions.Logging.ILogger
{
    private readonly string _categoryName;

    public SeleniumsvrLoggerAdapter(string categoryName)
    {
        _categoryName = categoryName;
    }

    /// <summary>スコープは使用しない</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>None 以外は全レベル有効</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <summary>
    /// .NET ログエントリを seleniumsvr.Logger に転送する。
    /// LogLevel → LogType のマッピング：
    ///   Trace / Debug   → LogType.Debug
    ///   Information     → LogType.System
    ///   Warning / Error / Critical → LogType.System
    /// </summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var logType = logLevel switch
        {
            LogLevel.Trace       => LogType.Debug,
            LogLevel.Debug       => LogType.Debug,
            LogLevel.Information => LogType.System,
            LogLevel.Warning     => LogType.System,
            LogLevel.Error       => LogType.System,
            LogLevel.Critical    => LogType.System,
            _                    => LogType.Nothing,
        };

        var message = formatter(state, exception);
        var text    = $"[{logLevel,-11}] [{_categoryName}] {message}";

        if (exception != null)
            text += Environment.NewLine + exception;

        Logger.Log(text, logType);
    }
}

/// <summary>
/// SeleniumsvrLoggerAdapter を生成する ILoggerProvider。
/// Program.cs で builder.Logging.AddProvider(new SeleniumsvrLoggerProvider()) として登録する。
/// </summary>
internal sealed class SeleniumsvrLoggerProvider : ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        => new SeleniumsvrLoggerAdapter(categoryName);

    public void Dispose() { }
}
