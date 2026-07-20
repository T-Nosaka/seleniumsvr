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
     Description("Find links on the page whose text contains the hint. Returns each matching link as 'Link Text<TAB>URL'. Use this to discover navigation links before clicking. Example: get_hrefs('テレビ') returns all TV-related links with their URLs. Then use navigate(url) or find_and_click with the link text.")]
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
    /// ページ内の要素を自然言語で検索し、セレクタを見つける
    /// </summary>
    /// <param name="query">探す要素の説明（例：「ログインボタン」「メールアドレス入力欄」）</param>
    /// <returns>見つかった要素のセレクタ（複数）、または "not found"</returns>
    [McpServerTool(Name = "find_element"),
     Description("Find elements on the page by natural language description. Search for buttons, links, inputs, images by what they do or what text they contain (e.g., 'Finance link', 'search button', 'email input'). Returns CSS selectors for matched elements. After finding, use click, input_text, or get_element_text with the returned selector. Better: use find_and_click or find_and_input for one-step operations.")]
    public string FindElement(
        [Description("Natural language description of what to find (e.g., 'Finance link', 'search button', 'email input field', 'Submit button').")]
        string query)
    {
        try
        {
            var pageSource = _session.GetPageSource();

            // クエリに基づいて要素セレクタを見つける
            var results = FindElementsByQuery(pageSource, query);

            if (results.Count == 0)
                return "not found";

            return string.Join("\n", results.Select((sel, i) => $"{i + 1}. {sel}"));
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }


    /// <summary>
    /// クエリに基づいて要素セレクタを見つける
    /// BrowserSessionの検索メソッドを試して、見つかったものを返す
    /// </summary>
    private List<string> FindElementsByQuery(string html, string query)
    {
        var results = new List<string>();

        // 検索戦略を順番に試す
        // 各戦略で見つかったら、その結果を返す

        // 1. 部分的なリンクテキストマッチ
        try
        {
            var selector = _session.FindPartialLinkText(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"a:contains('{query}')");
        }
        catch (Exception) { /* 見つからない */ }

        if (results.Count > 0) return results;

        // 2. ID 属性で完全一致
        try
        {
            var selector = _session.FindID(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"#{query}");
        }
        catch { }

        if (results.Count > 0) return results;

        // 3. Name 属性で検索
        try
        {
            var selector = _session.FindName(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"[name='{query}']");
        }
        catch { }

        if (results.Count > 0) return results;

        // 4. Class 名で検索
        try
        {
            var selector = _session.FindClassName(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($".{query}");
        }
        catch { }

        if (results.Count > 0) return results;

        // 5. Button テキストで完全一致
        try
        {
            var selector = _session.FindByButtonText(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"button:contains('{query}')");
        }
        catch { }

        if (results.Count > 0) return results;

        // 6. Button テキストで部分一致
        try
        {
            var selector = _session.FindByButtonTextPartial(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"button:contains('{query}')");
        }
        catch { }

        if (results.Count > 0) return results;

        // 7. Placeholder で検索（input フィールド）
        try
        {
            var selector = _session.FindByPlaceholder(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"input[placeholder*='{query}']");
        }
        catch { }

        if (results.Count > 0) return results;

        // 8. Label テキストから関連フォーム要素を検索
        try
        {
            var selector = _session.FindByLabelText(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"input[aria-label*='{query}']");
        }
        catch { }

        if (results.Count > 0) return results;

        // 9. 任意のテキストマッチ（最後の手段）
        try
        {
            var selector = _session.FindByText(query);
            if (!string.IsNullOrEmpty(selector))
                results.Add($"*:contains('{query}')");
        }
        catch { }

        if (results.Count > 0) return results;

        // すべて失敗した場合のフォールバック（正規表現ベース）
        return FindElementsByQueryRegex(html, query);
    }

    /// <summary>
    /// 正規表現ベースのフォールバック検索
    /// </summary>
    private List<string> FindElementsByQueryRegex(string html, string query)
    {
        var results = new List<string>();
        var lowerQuery = System.Text.RegularExpressions.Regex.Escape(query.ToLower());

        // パターン1: id属性を持つ要素でテキストがマッチ
        var idRegex = new System.Text.RegularExpressions.Regex(
            $@"<([a-z]+)[^>]*\bid=[""']?([^""'\s>]+)[""']?[^>]*>([^<]*{lowerQuery}[^<]*)<",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var idMatches = idRegex.Matches(html);
        foreach (System.Text.RegularExpressions.Match m in idMatches)
        {
            if (m.Groups.Count > 2 && !string.IsNullOrEmpty(m.Groups[2].Value))
                results.Add($"#{m.Groups[2].Value}");
        }

        if (results.Count > 0) return results;

        // パターン2: button/a/input タグのテキストまたは属性がマッチ
        var textRegex = new System.Text.RegularExpressions.Regex(
            $@"<(button|a|input)[^>]*>([^<]*{lowerQuery}[^<]*)<",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var textMatches = textRegex.Matches(html);
        if (textMatches.Count > 0)
        {
            results.Add(textMatches[0].Groups[1].Value);
            return results;
        }

        // パターン3: placeholder や value 属性がマッチ
        var attrRegex = new System.Text.RegularExpressions.Regex(
            $@"<input[^>]*(?:placeholder|value|name)[=""']([^""']*{lowerQuery}[^""']*)[""'][^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var attrMatches = attrRegex.Matches(html);
        if (attrMatches.Count > 0)
        {
            results.Add("input[type='text']");
            return results;
        }

        return results; // 何も見つからない
    }

    /// <summary>
    /// セレクタ一致要素をクリック
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "click"),
     Description("Click an element by CSS/XPath selector. Use ONLY when you already have the exact selector. PREFER find_and_click() instead - it handles finding and clicking in one action. For example: instead of find_element('Finance') then click(selector), just use find_and_click('Finance'). This tool is for advanced cases with known selectors only.")]
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
    /// セレクタ一致要素にテキストを入力
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="text">入力テキスト</param>
    /// <param name="by">セレクタ種別（Css / Xpath）</param>
    /// <param name="clear">入力前にフィールドをクリアするか</param>
    /// <returns>完了メッセージ、またはエラー文字列</returns>
    [McpServerTool(Name = "input_text"),
     Description("Type text into an input/textarea by selector. Use ONLY when you already have the exact selector. PREFER find_and_input() instead - it handles finding and typing in one action. For example: instead of find_element('email') then input_text(selector, email), just use find_and_input('email', 'user@example.com'). This tool is for advanced cases with known selectors only.")]
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
