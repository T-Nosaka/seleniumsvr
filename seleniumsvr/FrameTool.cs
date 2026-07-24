using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// iframe/frame 操作系 MCP ツール。
/// ページ本体が iframe 内に描画される構成（OCI コンソール等）に対応する。
/// get_page_text / find_element はトップ文書しか見ないため、
/// フレーム内を扱うにはこれらのツールでコンテキストを切り替えるか、
/// get_all_text で全フレームを一括取得する。
/// </summary>
[McpServerToolType]
public sealed class FrameTool
{
    /// <summary>ブラウザセッション本体（DI注入）</summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public FrameTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 現在のフレームコンテキスト内の iframe/frame を列挙する。
    /// </summary>
    [McpServerTool(Name = "list_frames"),
     Description("List all iframe/frame elements in the CURRENT frame context as JSON (index, id, name, src, sameOrigin, textLength). Use this first when get_page_text returns little or nothing but the screenshot shows content - the page body is likely rendered inside an iframe. 'sameOrigin:true' frames can be read via execute_script; any frame (same OR cross origin) can be entered with switch_to_frame or dumped with get_all_text.")]
    public string ListFrames()
    {
        try { return _session.ListFrames(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 指定した iframe/frame にコンテキストを切り替える。
    /// </summary>
    [McpServerTool(Name = "switch_to_frame"),
     Description("Switch the driver context INTO an iframe/frame. After switching, get_page_text / find_element / click / input_text all operate INSIDE that frame (works for cross-origin frames too, unlike execute_script). Navigating to a URL resets context back to the top document. Use switch_to_parent_frame or switch_to_default_content to get back out. Specify the frame via 'by': Index (0-based, e.g. '0'), IdOrName (the iframe id or name attribute), Css, or Xpath.")]
    public string SwitchToFrame(
        [Description("Frame locator. Interpreted per 'by': an index number ('0','1'...) for Index, the id/name for IdOrName, or a CSS/XPath selector.")]
        string selector,
        [Description("How to interpret 'selector': Index, IdOrName, Css, or Xpath. Default: Index.")]
        FrameTarget by = FrameTarget.Index)
    {
        Logger.Log($"SwitchToFrame [{selector}] [{by}]", LogType.Operation);
        try
        {
            _session.SwitchToFrame(selector, by);
            return $"switched to frame: {selector} (by {by})";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 一つ上の親フレームに戻る。
    /// </summary>
    [McpServerTool(Name = "switch_to_parent_frame"),
     Description("Move the driver context up one level to the parent frame. Use after switch_to_frame when you are done with a nested frame but want to stay in its parent (not all the way to the top).")]
    public string SwitchToParentFrame()
    {
        try
        {
            _session.SwitchToParentFrame();
            return "switched to parent frame";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// トップ文書に戻る。
    /// </summary>
    [McpServerTool(Name = "switch_to_default_content"),
     Description("Return the driver context to the top-level document (out of all frames). Call this when you are finished working inside iframes, before interacting with top-level page elements.")]
    public string SwitchToDefaultContent()
    {
        try
        {
            _session.SwitchToDefaultContent();
            return "switched to default content (top document)";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 全フレームを再帰的に辿って可視テキストを一括取得する。
    /// </summary>
    [McpServerTool(Name = "get_all_text"),
     Description("Get visible text from the top document AND every nested iframe/frame, recursively. This is the iframe-aware version of get_page_text: it actually switches into each frame at the WebDriver level, so it captures cross-origin iframe text that execute_script cannot reach. Output is labeled per frame like '===== [TOP > iframe#some-id] ====='. Use this whenever get_page_text looks empty/incomplete but the page clearly has content (e.g. OCI console, embedded apps). Context is restored to the top document afterwards.")]
    public string GetAllText(
        [Description("Maximum iframe nesting depth to traverse. Default 10.")]
        int maxDepth = 10)
    {
        try
        {
            var result = _session.GetAllText(maxDepth);
            return string.IsNullOrWhiteSpace(result) ? "(no text found)" : result;
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
