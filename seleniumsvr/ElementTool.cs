using ModelContextProtocol.Server;
using OpenQA.Selenium;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// DOM要素操作系 MCP ツール。
/// セレクタは CSS もしくは XPath を選択可能（既定: CSS）。
/// </summary>
[McpServerToolType]
public sealed class ElementTool
{
    /// <summary>
    /// ブラウザセッション本体（DI注入）
    /// </summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public ElementTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// リンク調査
    /// </summary>
    /// <param name="hint"></param>
    /// <returns></returns>
    [McpServerTool(Name = "get_hrefs"),
     Description("Find links on the page whose text contains the hint. Returns each matching link as 'Link Text<TAB>URL'. Use this to discover navigation links before clicking. Example: get_hrefs('テレビ') returns all TV-related links with their URLs. Then use navigate(url) to open one directly, or pass the URL to click() as an [href] selector.")]
    public string GetHrefs( string hint )
    {
        try
        {
            var result = _session.FindPartialLinkText(hint);
            return string.IsNullOrEmpty(result) ? "not found" : result;
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セクションリスト
    /// </summary>
    /// <returns></returns>
    [McpServerTool(Name = "list_section"),
     Description("This method safely lists all <section> elements on the current web page. It finds each section using Selenium and extracts its id, class, XPath, and the first 30 characters of its text. The collected data is returned as a single, newline-separated string.")]
    public string ListSection()
    {
        try
        {
            var result = _session.ListSection();
            return string.IsNullOrEmpty(result) ? "not found" : result;
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// タグツリー取得
    /// </summary>
    /// <param name="xpath"></param>
    /// <returns></returns>
    [McpServerTool(Name = "get_tree"),
     Description("This method creates a text representation of a DOM subtree from an XPath. It runs JavaScript to format each node with its tag, ID, and classes, using indentation to show hierarchy. The result is returned as a single string.")]
    public string GetTree(
        [Description("The xpath parameter is a string that specifies the starting point of the DOM tree. This expression identifies the element serving as the root of the output. Use /html to start from the document's absolute root.")]
        string xpath )
    {
        try
        {
            var result = _session.GetTree(xpath);
            return string.IsNullOrEmpty(result) ? "not found" : result;
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ページ内の要素を自然言語で検索し、検証済みセレクタを返す
    /// </summary>
    /// <param name="query">探す要素の説明（例：「ログインボタン」「メールアドレス入力欄」）</param>
    /// <param name="maxResults">返す候補の最大件数</param>
    /// <param name="includeFrames">iframe 内も走査するか</param>
    /// <returns>候補一覧、または "not found"</returns>
    [McpServerTool(Name = "find_element"),
     Description("Find elements by text or description (e.g. 'Login button', 'email input', '検索'). Matches against visible text, aria-label, placeholder, title, alt, value, name, id and role, with full-width/half-width and case normalization. Returns ranked candidates whose selectors are VERIFIED in the browser - each one is guaranteed to match exactly one element, so it can be passed directly to click / input_text / get_element_text. Also searches inside iframes by default; if a hit is in a frame, the output tells you which switch_to_frame call to make first. When nothing matches, use list_interactive_elements to see what is actually on the page.")]
    public string FindElement(
        [Description("Text or description of what to find (e.g. 'Login button', 'email input field', '検索').")]
        string query,
        [Description("Maximum number of candidates to return. Default: 10.")]
        int maxResults = 10,
        [Description("Also search inside iframes (recursively, cross-origin included). Default: true.")]
        bool includeFrames = true)
    {
        Logger.Log($"FindElement [{query}] [max={maxResults}] [frames={includeFrames}]", LogType.Operation);

        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "ERROR: query is empty. Use list_interactive_elements to enumerate elements instead.";

            var hits = _session.ScanElements(query, Math.Max(1, maxResults), includeFrames, false);
            if (hits.Count == 0)
                return "not found. Try a shorter/partial query, or use list_interactive_elements to see what is on the page.";

            return FormatHits(hits);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ページ上の操作可能要素を一覧する（クエリ不要）
    /// </summary>
    /// <param name="maxResults">返す件数の上限</param>
    /// <param name="includeFrames">iframe 内も走査するか</param>
    /// <returns>要素一覧</returns>
    [McpServerTool(Name = "list_interactive_elements"),
     Description("List the interactive elements on the page (links, buttons, inputs, selects, and ARIA widgets) with a VERIFIED selector for each, plus tag, type, label and state. Takes no query - use it when you do not yet know what the page offers, or when find_element returns nothing. This is a compact alternative to get_page_source: it gives you what you can actually click or type into, at a fraction of the size. Only visible elements are listed. Searches inside iframes by default and shows which switch_to_frame call each frame element needs.")]
    public string ListInteractiveElements(
        [Description("Maximum number of elements to return. Default: 40.")]
        int maxResults = 40,
        [Description("Also list elements inside iframes (recursively). Default: true.")]
        bool includeFrames = true)
    {
        Logger.Log($"ListInteractiveElements [max={maxResults}] [frames={includeFrames}]", LogType.Operation);

        try
        {
            var hits = _session.ScanElements("", Math.Max(1, maxResults), includeFrames, true);
            if (hits.Count == 0)
                return "(no interactive elements found)";

            return FormatHits(hits);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 走査結果を人間／LLMが読みやすい形に整形する。
    /// フレーム内の要素はフレームごとにまとめ、切り替え手順を明示する。
    /// </summary>
    /// <param name="hits">走査結果</param>
    /// <returns>整形済み文字列</returns>
    private static string FormatHits(List<BrowserSession.ElementHit> hits)
    {
        var sb = new System.Text.StringBuilder();
        var n = 0;
        string? currentFrame = null;

        foreach (var h in hits.OrderBy(h => h.FramePath == "TOP" ? 0 : 1)
                              .ThenBy(h => h.FramePath)
                              .ThenByDescending(h => h.Score))
        {
            if (h.FramePath != currentFrame)
            {
                currentFrame = h.FramePath;
                if (sb.Length > 0) sb.Append('\n');
                if (h.FramePath == "TOP")
                {
                    sb.Append("[TOP document]\n");
                }
                else
                {
                    sb.Append($"[FRAME: {h.FramePath}]\n")
                      .Append($"  -> call {h.SwitchHint} first, then use the selectors below.\n")
                      .Append("  -> call switch_to_default_content when done.\n");
                }
            }

            n++;
            var typePart = string.IsNullOrEmpty(h.Type) ? "" : $" type={h.Type}";
            var labelPart = string.IsNullOrEmpty(h.Label) ? "" : $" \"{h.Label}\"";
            sb.Append($"{n}. {h.Selector}  [{h.By}]  <{h.Tag}{typePart}>{labelPart}  ({h.State})\n");
        }

        return sb.ToString().TrimEnd();
    }
    /// <summary>
    /// セレクタ一致要素をクリック
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "click"),
     Description("Click an element by CSS/XPath selector. Obtain the selector first with find_element, get_tree, get_hrefs, or get_page_source. Use 'interact' instead when you need a double-click, right-click, or hover.")]
    public string Click(
        [Description("Selector string. Interpreted per 'by'. CSS by default (e.g. '#search-button').")]
        string selector,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        Logger.Log($"Click [{selector}] [{by}]", LogType.Operation);

        try
        {
            _session.Click(selector, by);
            return "clicked.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタ一致要素へのマウス操作（click/doubleclick/rightclick/hover）
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="action">実行するアクション</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "interact"),
     Description("Performs a mouse action on an element: click, doubleclick, rightclick, or hover. Use this instead of the plain 'click' tool when you need a double-click, a right-click (to open a context menu), or a hover (to trigger tooltips/dropdown menus). Requires the exact selector.")]
    public string Interact(
        [Description("Selector string. Interpreted per 'by'. CSS by default (e.g. '#menu-item').")]
        string selector,
        [Description("Action to perform: 'click', 'doubleclick', 'rightclick', or 'hover'.")]
        string action,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        Logger.Log($"Interact [{action}] [{selector}] [{by}]", LogType.Operation);

        try
        {
            _session.Interact(selector, by, action);
            return $"{action} performed.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// キーボードのキーを1つ押下する
    /// </summary>
    /// <param name="key">押下するキー名（特殊キー名 または 1文字）</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "press_key"),
     Description("Presses a keyboard key, sent to the currently focused element on the page. Special key names match OpenQA.Selenium.Keys field names (e.g. 'Enter', 'Tab', 'Escape', 'ArrowDown', 'ArrowUp', 'Backspace', 'Delete', 'Space'). Any other value (e.g. 'a') is sent as a literal character. Useful after input_text to submit a form with Enter, or to navigate menus with arrow keys.")]
    public string PressKey(
        [Description("Key to press (e.g. 'Enter', 'Tab', 'Escape', 'ArrowDown', or a single character like 'a').")]
        string key)
    {
        Logger.Log($"PressKey [{key}]", LogType.Operation);

        try
        {
            _session.PressKey(key);
            return "key pressed.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタ一致要素にテキストを入力
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="text">入力テキスト</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <param name="clear">入力前にフィールドをクリアするか</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "input_text"),
     Description("Type text into an input/textarea by selector. Obtain the selector first with find_element, get_tree, or get_page_source. Follow with press_key('Enter') to submit a form.")]
    public string InputText(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Text to type into the element.")]
        string text,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css,
        [Description("Whether to clear the field before typing. Default: true.")]
        bool clear = true)
    {
        Logger.Log($"InputText [{selector}] [{text}] [{by}]", LogType.Operation);

        try
        {
            _session.InputText(selector, by, text, clear);
            return "input sent.";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタ一致要素の可視テキストを取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>要素の可視テキスト、またはエラー文字列</returns>
    [McpServerTool(Name = "get_element_text"),
     Description("Gets the visible text content of a single element by CSS/XPath selector. Use ONLY when you already have the exact selector (e.g. from find_element). For getting text from multiple matching elements at once, use get_multiple_elements_text instead.")]
    public string GetElementText(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        try { return _session.GetElementText(selector, by); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタ一致要素の属性値を取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <param name="attribute">属性名</param>
    /// <returns>属性値、またはエラー文字列</returns>
    [McpServerTool(Name = "get_element_attribute"),
     Description("Gets an attribute value from an element by CSS/XPath selector (e.g. 'href', 'value', 'class', 'disabled', 'data-*'). Use this to inspect link destinations, current input values, or element state without relying on get_page_source.")]
    public string GetElementAttribute(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Attribute name to read (e.g. 'href', 'value', 'class').")]
        string attribute,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        try
        {
            var value = _session.GetElementAttribute(selector, by, attribute);
            return value ?? "(null)";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタに一致する要素数を取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>一致件数、またはエラー文字列</returns>
    [McpServerTool(Name = "count_elements"),
     Description("Counts how many elements on the page match a CSS/XPath selector. Useful for checking whether a list has loaded, how many rows a table has, or whether a selector is ambiguous before calling click/input_text.")]
    public string CountElements(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        try { return _session.CountElements(selector, by).ToString(); }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタに一致するすべての要素の可視テキストを取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>各要素のテキスト（改行区切り）、またはエラー文字列</returns>
    [McpServerTool(Name = "get_multiple_elements_text"),
     Description("Gets the visible text of every element matching a CSS/XPath selector, one per line, in DOM order. Use this for lists, table cells, or repeated items (e.g. all '.product-name' elements) instead of calling get_element_text in a loop.")]
    public string GetMultipleElementsText(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css)
    {
        try
        {
            var texts = _session.GetMultipleElementsText(selector, by);
            return texts.Count == 0 ? "(no matching elements)" : string.Join("\n", texts);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// セレクタ一致要素の出現を待機
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <param name="timeoutSeconds">タイムアウト秒数</param>
    /// <returns>"true" / "false"、またはエラー文字列</returns>
    [McpServerTool(Name = "wait_for_element"),
     Description("Wait for an element to appear in the DOM (useful for dynamic pages). First find the element with find_element to get its selector, then use this to wait for it after navigation or interaction. Returns 'true' if found within timeout, 'false' if not.")]
    public string WaitForElement(
        [Description("Selector string. Interpreted per 'by'. CSS by default.")]
        string selector,
        [Description("Selector type: 'Css' (default) or 'Xpath'.")]
        SelectorType by = SelectorType.Css,
        [Description("Timeout in seconds. Default: 10.")]
        int timeoutSeconds = 10)
    {
        try { return _session.WaitForElement(selector, by, timeoutSeconds) ? "true" : "false"; }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
