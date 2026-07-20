using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// 任意 JavaScript 実行系 MCP ツール。
/// 強力だが危険度も高いので、使用は限定的にすること。
/// </summary>
[McpServerToolType]
public sealed class ScriptTool
{
    /// <summary>
    /// ブラウザセッション本体（DI注入）
    /// </summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public ScriptTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 任意JavaScriptを現ページコンテキストで実行
    /// </summary>
    /// <param name="script">JSソース（return で値を返す）</param>
    /// <returns>実行結果（文字列 or JSON）、またはエラー文字列</returns>
    [McpServerTool(Name = "execute_script"),
     Description("Execute JavaScript on the current page. Only use as a last resort when find_element, click, input_text, or other tools cannot solve the problem. Examples: 'return document.title;' to read page data, 'document.querySelector(\"button\").click();' to trigger actions. Return values are serialized as JSON. WARNING: Use with extreme caution - only for advanced scenarios.")]
    public string ExecuteScript(
        [Description("JavaScript code to execute. Must use 'return' to return data. Example: 'return document.querySelectorAll(\"a\").length;' or 'return {title: document.title, url: window.location.href};'")]
        string script)
    {
        try { return _session.ExecuteScript(script); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
