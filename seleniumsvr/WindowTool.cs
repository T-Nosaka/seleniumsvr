using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// ブラウザの window/tab 制御系 MCP ツール。
/// クリックやJS実行で開いた新 window には自動アタッチされるが、
/// 任意の window に明示的に切替えたい場合や、現在の状況を把握したいときに使用する。
/// </summary>
[McpServerToolType]
public sealed class WindowTool
{
    /// <summary>ブラウザセッション本体（DI注入）</summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public WindowTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 全 window/tab の一覧を返す
    /// </summary>
    /// <returns>handle / title / url / isCurrent を含む JSON 配列</returns>
    [McpServerTool(Name = "list_windows"),
     Description("List all open browser windows and tabs. Returns JSON with handle, title, URL, and which is current. Use after clicking a link that opens in a new window to see all windows, then switch_window to the one you want, or close_window to close popups you don't need. New windows auto-attach but use this to verify or switch manually.")]
    public string ListWindows()
    {
        try
        {
            var list = _session.ListWindows();
            return JsonConvert.SerializeObject(list, Formatting.Indented);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 指定の handle の window に切替える
    /// </summary>
    /// <param name="handle">list_windows で得た handle</param>
    /// <returns>切替え結果メッセージ</returns>
    [McpServerTool(Name = "switch_window"),
     Description("Switch to a different window or tab. First use list_windows to see all windows and get the handle you want, then switch_window to focus it. After switching, screenshot to verify you're on the right window, then navigate/click/find_element on that window. All operations target the switched window until you switch again.")]
    public string SwitchWindow(
        [Description("Window handle returned by 'list_windows'.")]
        string handle)
    {
        try
        {
            _session.SwitchToWindow(handle);
            return $"switched to: {handle}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在の window を閉じて、残った window のうち最後のものに切替える
    /// </summary>
    /// <returns>結果メッセージ</returns>
    [McpServerTool(Name = "close_window"),
     Description("Close the current window or tab and automatically switch to another. Use when you need to close popups or unwanted windows after clicking links. Cannot close the last remaining window - use close_browser if you want to fully close Chrome. After closing, screenshot to verify which window you're now on.")]
    public string CloseWindow()
    {
        try
        {
            _session.CloseCurrentWindow();
            return "window closed.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
