using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// ページ情報取得系 MCP ツール（読み取り専用）。
/// </summary>
[McpServerToolType]
public sealed class PageInfoTool
{
    /// <summary>ブラウザセッション本体（DI注入）</summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public PageInfoTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 現在ページのタイトル取得
    /// </summary>
    /// <returns>タイトル、またはエラー文字列</returns>
    [McpServerTool(Name = "get_title"),
     Description("Get the current page's title (from <title> tag). Useful to verify you're on the right page after navigation.")]
    public string GetTitle()
    {
        try { return _session.GetTitle(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在ページのURL取得
    /// </summary>
    /// <returns>URL、またはエラー文字列</returns>
    [McpServerTool(Name = "get_current_url"),
     Description("Get the current page's URL (after any redirects). Use after navigation to verify you're on the right page, extract the URL to navigate elsewhere, or find the current location in browser history. Essential after go_back/go_forward to confirm which page you're viewing.")]
    public string GetCurrentUrl()
    {
        try { return _session.GetCurrentUrl(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在ページの可視テキスト取得
    /// </summary>
    /// <returns>可視テキスト、またはエラー文字列</returns>
    [McpServerTool(Name = "get_page_text"),
     Description("Get the visible text content of the current page (document.body.innerText). Use this after navigation to read page content, find specific text to search for with find_element, verify page loaded correctly, or extract information for analysis. Always get_page_text after navigation to understand what's on the page before interacting with elements.")]
    public string GetPageText()
    {
        try { return _session.GetPageText(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在ページのHTMLソース取得
    /// </summary>
    /// <returns>HTMLソース、またはエラー文字列</returns>
    [McpServerTool(Name = "get_page_source"),
     Description("Get the full HTML source of the current page. Use when you need to find elements by ID, class, or HTML structure that aren't visible as text. Heavy output — prefer get_page_text for reading, find_element for locating specific elements, or get_element_attribute/text for targeted extraction. Only use when you need the raw HTML for debugging or complex element discovery.")]
    public string GetPageSource()
    {
        try { return _session.GetPageSource(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在ページのスクリーンショットを MCP 画像ブロック（ImageContentBlock）で返す。
    /// 文字列 base64 ではなく画像型として返すため、MCP クライアントが直接レンダリングできる。
    /// ※OCI AI だと、Tool応答で、画像が使えない
    /// </summary>
    /// <returns>PNG 画像ブロック</returns>
    [McpServerTool(Name = "screenshot"),
     Description("Capture a screenshot of the current page and return it as a PNG image. Use after navigation to visually verify the page loaded correctly, document the state before taking actions, check if elements are visible, or capture results for reporting. Always screenshot after major navigation or actions to confirm page state.")]
    public ImageContentBlock Screenshot()
    {
        var bytes = _session.ScreenshotBytes();
        return ImageContentBlock.FromBytes(bytes, "image/png");
    }

}
