using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// ブラウザの alert / confirm / prompt ダイアログ操作系 MCP ツール。
/// ダイアログはクリックやフォーム送信の直後に非同期で出現し、表示中は他のツール
/// （click / get_page_text 等）がすべてブロックされるため、専用ツールで先に処理する必要がある。
/// </summary>
[McpServerToolType]
public sealed class AlertTool
{
    /// <summary>ブラウザセッション本体（DI注入）</summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public AlertTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// alert / confirm / prompt ダイアログを処理する。
    /// </summary>
    /// <param name="action">'accept' / 'dismiss' / 'get_text' / 'send_text'</param>
    /// <param name="text">'send_text' の場合に入力するテキスト</param>
    /// <param name="timeoutSeconds">ダイアログ出現待ちのタイムアウト秒数</param>
    /// <returns>結果文字列、またはエラー文字列</returns>
    [McpServerTool(Name = "alert"),
     Description("Handles a browser alert, confirm, or prompt dialog. Call this immediately after an action (e.g. click) that you expect to trigger a dialog - it waits for the dialog to appear before acting. 'accept' clicks OK, 'dismiss' clicks Cancel/closes it, 'get_text' reads the dialog's message without closing it, 'send_text' types into a prompt() dialog's input field (call accept afterwards to submit it). While a dialog is open, other tools like click/get_page_text will fail, so always resolve it with this tool first.")]
    public string Alert(
        [Description("Action to perform: 'accept', 'dismiss', 'get_text', or 'send_text'.")]
        string action,
        [Description("Text to type into a prompt() dialog. Required only for 'send_text'.")]
        string? text = null,
        [Description("Max seconds to wait for the dialog to appear. Default: 5.")]
        int timeoutSeconds = 5)
    {
        Logger.Log($"Alert [{action}] [{timeoutSeconds}s]", LogType.Operation);

        try
        {
            return _session.HandleAlert(action, text, timeoutSeconds);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
