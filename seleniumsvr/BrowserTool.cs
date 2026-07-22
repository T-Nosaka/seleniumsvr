using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// ブラウザのライフサイクル / ナビゲーション系 MCP ツール。
/// </summary>
[McpServerToolType]
public sealed class BrowserTool
{
    /// <summary>
    /// ブラウザセッション本体（DI注入）
    /// </summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public BrowserTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 利用可能なブラウザ定義の一覧を返す。
    /// </summary>
    /// <returns>定義名・種別・プロファイルパスの一覧、またはエラー文字列</returns>
    [McpServerTool(Name = "list_browser"),
     Description("List the available browser definitions (names) configured in webdriverinfo.json, with each definition's browser type and profile path. The current selection is marked with '*'. Use this to discover which profile name to pass to prepare_browser.")]
    public string ListBrowser()
    {
        try { return _session.ListBrowsers(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ブラウザを準備する（宣言的 acquire）。定義名でプロファイルを選び、排他ロックを取得する。
    /// </summary>
    /// <param name="profile">ブラウザ定義名（list_browser で確認）。省略時は "default"</param>
    /// <returns>結果メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "prepare_browser"),
     Description("Acquire and prepare a browser for this session. Pass a browser definition name from list_browser (e.g. a profile that already holds the target site's cookies); omit to use 'default'. This launches the browser and takes an exclusive lock on its profile, so the SAME profile cannot be driven by two sessions at once. If already prepared with the same profile this is a no-op; if a different profile is active, release_browser must be called first. Call this before navigate when you need a specific profile.")]
    public string PrepareBrowser(
        [Description("Browser definition name from list_browser. Omit for 'default'.")]
        string? profile = null)
    {
        try { return _session.Prepare(profile); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ブラウザを終了し、プロファイルロックを解放する（宣言的 release）。
    /// </summary>
    /// <returns>結果メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "release_browser"),
     Description("Release the browser prepared by prepare_browser: quit it and free the exclusive profile lock so another session can use that profile. Recommended when you are done so the profile is freed promptly, but not strictly required - the lock is always released automatically when the server process exits.")]
    public string ReleaseBrowser()
    {
        try { return _session.Release(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在のセッション状態（起動有無・選択プロファイル・ロック状態・現在URL）を返す。
    /// </summary>
    /// <returns>状態文字列、またはエラー文字列</returns>
    [McpServerTool(Name = "session_status"),
     Description("Report the current session state: whether a browser is prepared, which profile definition is selected, its browser type and profile path, whether the profile lock is held, and the current page title/URL. Use to check what this session is driving before acting.")]
    public string SessionStatus()
    {
        try { return _session.GetStatus(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 指定URLへ遷移（未起動時はBrowserを自動起動）
    /// </summary>
    /// <param name="url">絶対URL</param>
    /// <returns>タイトルと最終URL、またはエラー文字列</returns>
    [McpServerTool(Name = "navigate"),
     Description("Open an absolute URL in Browser. Launches Browser if needed. Always use navigate as the first step, then screenshot to verify page loaded, then get_page_text or get_current_url to confirm you're on the right page. After navigation, use find_element to locate and click links, or input_text to fill forms. Returns page title and final URL after redirects.")]
    public string Navigate(
        [Description("Absolute URL to navigate to (e.g. https://www.example.com/). Must start with http:// or https://.")]
        string url)
    {
        try
        {
            var (title, finalUrl) = _session.Navigate(url);
            return $"title: {title}\nurl: {finalUrl}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 履歴を一つ戻る
    /// </summary>
    /// <returns>タイトルと最終URL、またはエラー文字列</returns>
    [McpServerTool(Name = "go_back"),
     Description("Navigate back in browser history. After going back, use screenshot to verify the previous page loaded, then get_current_url to confirm location. Returns page title and URL of the previous page.")]
    public string GoBack()
    {
        try
        {
            var (title, finalUrl) = _session.GoBack();
            return $"title: {title}\nurl: {finalUrl}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 履歴を一つ進む
    /// </summary>
    /// <returns>タイトルと最終URL、またはエラー文字列</returns>
    [McpServerTool(Name = "go_forward"),
     Description("Navigate forward in browser history. Use after go_back when you need to revisit a page. After going forward, use screenshot to verify the next page loaded, then get_current_url to confirm location. Returns page title and URL of the next page.")]
    public string GoForward()
    {
        try
        {
            var (title, finalUrl) = _session.GoForward();
            return $"title: {title}\nurl: {finalUrl}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在ページを再読込
    /// </summary>
    /// <returns>タイトルと最終URL、またはエラー文字列</returns>
    [McpServerTool(Name = "reload"),
     Description("Reload the current page. Use when the page isn't responding, you need fresh data, or content didn't load correctly. After reloading, screenshot to verify the page loaded, then use get_page_text or find_element to interact with the refreshed content. Returns page title and URL.")]
    public string Reload()
    {
        try
        {
            var (title, finalUrl) = _session.Reload();
            return $"title: {title}\nurl: {finalUrl}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ブラウザを終了してセッションをリセット
    /// </summary>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "close_browser"),
     Description("Quit Browser and reset the session completely. Use when you need a clean fresh start, finished with automation, or to clear all cookies/cache. The next navigate call will launch a fresh Browser instance. Use close_browser between completely different tasks to ensure no state carries over.")]
    public string CloseBrowser()
    {
        try
        {
            _session.Close();
            return "browser closed.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
