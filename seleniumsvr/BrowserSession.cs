using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Interactions;

using OpenQA.Selenium.Support.UI;
using System.Collections.ObjectModel;

namespace seleniumsvr;

/// <summary>
/// セレクタ種別。MCPツール側では文字列 "Css" / "Xpath" として露出する。
/// </summary>
public enum SelectorType
{
    /// <summary>CSSセレクタ</summary>
    Css,

    /// <summary>XPath</summary>
    Xpath,
}

/// <summary>
/// iframe/frame の指定方法。MCPツール側では文字列として露出する。
/// </summary>
public enum FrameTarget
{
    /// <summary>CSSセレクタで iframe 要素を指定</summary>
    Css,

    /// <summary>XPath で iframe 要素を指定</summary>
    Xpath,

    /// <summary>フレームのインデックス（0始まり）で指定</summary>
    Index,

    /// <summary>iframe の id または name 属性で指定</summary>
    IdOrName,
}

/// <summary>
/// ブラウザの window/tab の情報。
/// </summary>
/// <param name="Handle">Selenium 内部の window ハンドル（switch_window で指定）</param>
/// <param name="Title">window のタイトル</param>
/// <param name="Url">window の現在URL</param>
/// <param name="IsCurrent">現在アクティブな window か</param>
public sealed record WindowInfo(string Handle, string Title, string Url, bool IsCurrent);

/// <summary>
/// Browser / WebDriver のセッションをプロセス寿命で管理するシングルトン。
/// - 初回 Navigate 呼び出しで Browser を起動（遅延初期化）
/// - Close で Quit、続く Navigate で再起動可能
/// - プロセス終了時に Dispose で確実に Quit
/// - Selenium WebDriver は非スレッドセーフなので全操作はロックで直列化
/// </summary>
public sealed class BrowserSession : IDisposable
{
    /// <summary>
    /// ブラウザ情報ファイル
    /// </summary>
    public static string? webdriverinfopath = null;


    /// <summary>排他制御用ロックオブジェクト</summary>
    private readonly object _gate = new();

    /// <summary>WebDriver本体。未起動時はnull</summary>
    private WebDriver? _driver;

    /// <summary>待機用WebDriverWait。_driverと同じライフサイクル</summary>
    private WebDriverWait? _wait;

    /// <summary>現在のダウンロードフォルダ。EnsureStartedLocked で ChromeInfo から初期化される</summary>
    private string _downloadDir = string.Empty;

    /// <summary>
    /// 直近の操作開始時点で存在していた window ハンドル一覧。
    /// 操作後に新しい window が増えていれば自動で切替える（自動アタッチ）。
    /// </summary>
    private string[] _previousHandles = Array.Empty<string>();

    /// <summary>プロファイル排他ロック用ファイルストリーム。未取得時はnull</summary>
    private FileStream? _profileLock;

    /// <summary>現在選択中のブラウザ定義名。未prepare時はnull</summary>
    private string? _selectedName;

    // ---------- ライフサイクル / ナビゲーション ----------

    /// <summary>
    /// 指定URLへ遷移。ブラウザ未起動なら自動で起動する。
    /// Chrome ウィンドウが手動で閉じられるなどセッションが死亡した場合は
    /// ドライバをリセットして Browser を再起動し、自動復旧する。
    /// </summary>
    /// <param name="url">絶対URL（http/https）</param>
    /// <returns>遷移後のタイトルと最終URL</returns>
    public (string Title, string Url) Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is empty.", nameof(url));

        lock (_gate)
        {
            EnsureStartedLocked();
            try
            {
                _driver!.Navigate().GoToUrl(url);
            }
            catch (WebDriverException ex)
            {
                // セッション死亡時（ブラウザを手動で閉じた場合など）は
                // ドライバをリセットして Browser を再起動してリトライ
                Logger.Log($"Session lost, restarting Browser. ({ex.Message})", LogType.System);
                ResetSessionLocked();
                EnsureStartedLocked();
                _driver!.Navigate().GoToUrl(url);
            }
            WaitReadyLocked();
            AutoAttachLocked();
            return (_driver.Title, _driver.Url);
        }
    }

    /// <summary>
    /// ブラウザ履歴を一つ戻る
    /// </summary>
    /// <returns>遷移後のタイトルと最終URL</returns>
    public (string Title, string Url) GoBack()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            _driver!.Navigate().Back();
            WaitReadyLocked();
            AutoAttachLocked();
            return (_driver.Title, _driver.Url);
        }
    }

    /// <summary>
    /// ブラウザ履歴を一つ進む
    /// </summary>
    /// <returns>遷移後のタイトルと最終URL</returns>
    public (string Title, string Url) GoForward()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            _driver!.Navigate().Forward();
            WaitReadyLocked();
            AutoAttachLocked();
            return (_driver.Title, _driver.Url);
        }
    }

    /// <summary>
    /// 現在のページを再読込
    /// </summary>
    /// <returns>再読込後のタイトルと最終URL</returns>
    public (string Title, string Url) Reload()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            _driver!.Navigate().Refresh();
            WaitReadyLocked();
            AutoAttachLocked();
            return (_driver.Title, _driver.Url);
        }
    }

    /// <summary>
    /// ブラウザを終了してセッションをリセットする。プロファイルロックも解放する。
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            try { _driver?.Quit(); } catch { /* best-effort */ }
            _driver?.Dispose();
            _driver = null;
            _wait = null;
            ReleaseProfileLockLocked();
        }
    }

    /// <summary>
    /// ブラウザを準備する（宣言的 acquire）。
    /// 指定した定義名のブラウザ/プロファイルでセッションを構成し、永続プロファイルなら排他ロックを取得する。
    /// すでに同名で準備済みなら no-op。別名で起動中なら例外（先に release が必要）。
    /// </summary>
    /// <param name="name">ブラウザ定義名。null/空なら "default"</param>
    /// <returns>結果メッセージ</returns>
    public string Prepare(string name)
    {
        lock (_gate)
        {
            var target = string.IsNullOrWhiteSpace(name) ? "default" : name!.Trim();

            if (_driver != null)
            {
                if (string.Equals(_selectedName, target, StringComparison.OrdinalIgnoreCase))
                    return $"already prepared: {_selectedName}";
                throw new InvalidOperationException(
                    $"別のプロファイル '{_selectedName}' で起動中です。先に release_browser を呼んでください。");
            }

            var previous = _selectedName;
            _selectedName = target;
            try
            {
                EnsureStartedLocked();
            }
            catch
            {
                _selectedName = previous;  // 失敗時は選択状態を戻す
                throw;
            }
            return $"prepared: {_selectedName}";
        }
    }

    /// <summary>
    /// ブラウザを終了し、プロファイルロックを解放して選択状態をクリアする（宣言的 release）。
    /// best-effort。呼ばれなくてもプロセス終了時に Dispose で必ず解放される。
    /// </summary>
    /// <returns>結果メッセージ</returns>
    public string Release()
    {
        lock (_gate)
        {
            var was = _selectedName;
            try { _driver?.Quit(); } catch { /* best-effort */ }
            _driver?.Dispose();
            _driver = null;
            _wait = null;
            ReleaseProfileLockLocked();
            _selectedName = null;
            _previousHandles = Array.Empty<string>();
            return was == null ? "no active session." : $"released: {was}";
        }
    }

    /// <summary>
    /// 現在のセッション状態を返す（起動有無・選択プロファイル・ロック状態・現在URL等）。
    /// </summary>
    /// <returns>状態文字列</returns>
    public string GetStatus()
    {
        lock (_gate)
        {
            var running = _driver != null;
            var name = _selectedName ?? "(none)";
            string btype = "(unknown)";
            string profile = "(none)";
            try
            {
                var info = ResolveInfo(_selectedName ?? "default");
                btype = info.BrowserType;
                profile = ExtractProfilePath(info) ?? "(ephemeral)";
            }
            catch { /* 定義が無い場合はそのまま */ }

            string title = "-", url = "-";
            if (running)
            {
                try { title = _driver!.Title; url = _driver.Url; } catch { /* セッション不安定時 */ }
            }

            return
                $"prepared: {running}\n" +
                $"profile_name: {name}\n" +
                $"browser_type: {btype}\n" +
                $"profile_path: {profile}\n" +
                $"profile_locked: {_profileLock != null}\n" +
                $"current_title: {title}\n" +
                $"current_url: {url}";
        }
    }

    /// <summary>
    /// 利用可能なブラウザ定義の一覧を返す。
    /// </summary>
    /// <returns>定義名・種別・プロファイルパスの一覧（現在選択中は * 付き）</returns>
    public string ListBrowsers()
    {
        lock (_gate)
        {
            var all = LoadAllBrowsers();
            if (all.Count == 0) return "(no browser definitions found)";

            var lines = all.Select(kv =>
            {
                var pth = ExtractProfilePath(kv.Value) ?? "(ephemeral)";
                var cur = string.Equals(kv.Key, _selectedName, StringComparison.OrdinalIgnoreCase) ? " *" : "";
                return $"{kv.Key}{cur}\ttype={kv.Value.BrowserType}\tprofile={pth}";
            });
            return string.Join("\n", lines);
        }
    }

    // ---------- ページ情報 ----------

    /// <summary>
    /// タグツリー取得
    /// </summary>
    /// <param name="xpath"></param>
    /// <returns></returns>
    public String GetTree(string xpath)
    {
        lock (_gate)
        {
            RequireStartedLocked();

            return GetTreeInner(xpath) ?? "";
        }
    }

    /// <summary>
    /// セクションリスト取得
    /// </summary>
    /// <returns></returns>
    public String ListSection()
    {
        lock (_gate)
        {
            RequireStartedLocked();

            var lines = new List<string>();
            var selectiontags = _driver!.FindElements(By.TagName("section"));
            foreach (var item in selectiontags)
            {
                var idstr = item.GetAttribute("id") ?? "";
                var classstr = item.GetAttribute("class") ?? "";
                var text = item.Text.Substring(0, 30);
                var xpath = GetXPath(item) ?? "";

                lines.Add($"id={idstr},class={classstr},xpath={xpath},text={text}");
            }

            return string.Join("\n", lines);
        }
    }


    /// <summary>
    /// 部分一致リンク取得
    /// </summary>
    /// <param name="hint"></param>
    /// <returns></returns>
    /// <summary>
    /// 部分一致するリンクテキストを持つ要素の href 一覧を返す
    /// </summary>
    /// <param name="hint">リンクテキストの一部</param>
    /// <returns>マッチした href 値の一覧（タブ区切り: テキスト → href）</returns>
    public String FindPartialLinkText( string hint )
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var elements = _driver!.FindElements(By.PartialLinkText(hint));
            if (elements.Count == 0) return "";
            var lines = elements
                .Select(el => $"{el.Text}\t{el.GetAttribute("href")}")
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join("\n", lines);
        }
    }

    public String FindID(string id)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.Id(id)).ToString() ?? "";
        }
    }

    public String FindName(string name)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.Name(name)).ToString() ?? "";
        }
    }

    public String FindClassName(string classname)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.ClassName(classname)).ToString() ?? "";
        }
    }

    public String FindTagName(string tagname)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.TagName(tagname)).ToString() ?? "";
        }
    }

    /// <summary>
    /// input要素の placeholder 属性で検索
    /// </summary>
    /// <param name="placeholder">placeholder の値（部分一致）</param>
    /// <returns>見つかった要素の情報、見つからない場合は空文字列</returns>
    public String FindByPlaceholder(string placeholder)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.XPath($"//input[contains(@placeholder, '{placeholder}')]")).ToString() ?? "";
        }
    }

    /// <summary>
    /// button 要素をテキストで検索（完全一致）
    /// </summary>
    /// <param name="buttonText">ボタンのテキスト</param>
    /// <returns>見つかった要素の情報、見つからない場合は空文字列</returns>
    public String FindByButtonText(string buttonText)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.XPath($"//button[text()='{buttonText}']")).ToString() ?? "";
        }
    }

    /// <summary>
    /// button 要素をテキストで検索（部分一致）
    /// </summary>
    /// <param name="buttonTextHint">ボタンテキストの一部</param>
    /// <returns>見つかった要素の情報、見つからない場合は空文字列</returns>
    public String FindByButtonTextPartial(string buttonTextHint)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.XPath($"//button[contains(text(), '{buttonTextHint}')]")).ToString() ?? "";
        }
    }

    /// <summary>
    /// label 要素のテキストから関連する input を検索
    /// </summary>
    /// <param name="labelText">label のテキスト</param>
    /// <returns>見つかった input 要素の情報、見つからない場合は空文字列</returns>
    public String FindByLabelText(string labelText)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var label = _driver!.FindElement(By.XPath($"//label[contains(text(), '{labelText}')]"));
            var forAttr = label.GetAttribute("for");
            if (!string.IsNullOrEmpty(forAttr))
                return _driver!.FindElement(By.Id(forAttr)).ToString() ?? "";

            // label の for 属性がない場合は、次の input を探す
            return _driver!.FindElement(By.XPath($"//label[contains(text(), '{labelText}')]/following-sibling::input[1]")).ToString() ?? "";
        }
    }

    /// <summary>
    /// 指定したテキストを含む任意のタグを検索
    /// </summary>
    /// <param name="text">検索するテキスト</param>
    /// <returns>見つかった要素の情報、見つからない場合は空文字列</returns>
    public String FindByText(string text)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return _driver!.FindElement(By.XPath($"//*[contains(text(), '{text}')]")).ToString() ?? "";
        }
    }

    /// <summary>
    /// 現在ページのタイトルを取得
    /// </summary>
    /// <returns>タイトル文字列</returns>
    public string GetTitle()
    {
        lock (_gate) { RequireStartedLocked(); return _driver!.Title; }
    }

    /// <summary>
    /// 現在ページのURLを取得（リダイレクト後の最終URL）
    /// </summary>
    /// <returns>URL文字列</returns>
    public string GetCurrentUrl()
    {
        lock (_gate) { RequireStartedLocked(); return _driver!.Url; }
    }

    /// <summary>
    /// 現在ページの可視テキスト（document.body.innerText）を取得
    /// </summary>
    /// <returns>可視テキスト</returns>
    public string GetPageText()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var js = (IJavaScriptExecutor)_driver!;
            var txt = js.ExecuteScript("return document.body ? document.body.innerText : '';") as string;
            return txt ?? string.Empty;
        }
    }

    /// <summary>
    /// 現在ページのHTMLソースを取得
    /// </summary>
    /// <returns>HTMLソース</returns>
    public string GetPageSource()
    {
        lock (_gate) { RequireStartedLocked(); return _driver!.PageSource; }
    }

    /// <summary>
    /// 現在ページのスクリーンショットをバイト列（PNG）で取得
    /// </summary>
    /// <returns>PNG バイト列</returns>
    public byte[] ScreenshotBytes()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return ((ITakesScreenshot)_driver!).GetScreenshot().AsByteArray;
        }
    }

    // ---------- 要素操作 ----------

    /// <summary>
    /// XPath取得
    /// </summary>
    /// <param name="elm"></param>
    /// <returns></returns>
    public string? GetXPath(IWebElement elm)
    {
        string GET_XPATH_JS = """
function getXPath(elm) {
    var allNodes = document.getElementsByTagName('*');
    for (var segs = []; elm && elm.nodeType == 1; elm = elm.parentNode) {
        if (elm.hasAttribute('id')) {
            var uniqueIdCount = 0;
            for (var n = 0; n < allNodes.length; n++) {
                if (allNodes[n].hasAttribute('id') && allNodes[n].id == elm.id) uniqueIdCount++;
                if (uniqueIdCount > 1) break;
            };
            if (uniqueIdCount == 1) {
                segs.unshift('//*[@id="' + elm.getAttribute('id') + '"]');
                return segs.join('/');
            } else {
                segs.unshift(elm.localName.toLowerCase() + '[@id="' + elm.getAttribute('id') + '"]');
            }
        } else {
            for (var i = 1, sib = elm.previousSibling; sib; sib = sib.previousSibling) {
                if (sib.localName == elm.localName) i++;
            };
            segs.unshift(elm.localName.toLowerCase() + '[' + i + ']');
        }
    };
    return segs.length ? '/' + segs.join('/') : null;
}
return getXPath(arguments[0]);
""";

        var result = ((IJavaScriptExecutor)_driver!).ExecuteScript(GET_XPATH_JS, elm) ?? "";
        return result.ToString();
    }

    /// <summary>
    /// ツリー構造取得
    /// </summary>
    /// <param name="xpath"></param>
    /// <returns></returns>
    public string? GetTreeInner(string xpath)
    {
        string GET_SUBTREE_JS = """
function buildSubTree(element, indent) {
    if (!element || element.nodeType !== 1) {
        return '';
    }
    let idStr = element.id ? `#${element.id}` : '';

    // 1. String()で明示的に文字列に変換する
    // 2. || '' で、class属性が存在しない場合も空文字列として安全に扱う
    let classNameStr = String(element.className || '');
    let classStr = classNameStr ? `.${classNameStr.trim().replace(/\s+/g, '.')}` : '';

    let line = `${indent}${element.tagName.toLowerCase()}${idStr}${classStr}\n`;

    for (const child of element.children) {
        line += buildSubTree(child, indent + '  ');
    }
    return line;
}
return buildSubTree(arguments[0], '');
""";

        var elem = _driver!.FindElement(By.XPath(xpath));
        var result = ((IJavaScriptExecutor)_driver!).ExecuteScript(GET_SUBTREE_JS, elem) ?? "";
        return result.ToString();
    }


    /// <summary>
    /// セレクタ一致要素をクリック
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    public void Click(string selector, SelectorType by)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            FindLocked(selector, by).Click();
            AutoAttachLocked();
        }
    }

    /// <summary>
    /// セレクタ一致要素に対してマウス操作を実行する（click / doubleclick / rightclick / hover）
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <param name="action">実行するアクション（click/doubleclick/rightclick/hover、大文字小文字区別なし）</param>
    public void Interact(string selector, SelectorType by, string action)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var el = FindLocked(selector, by);
            var actions = new Actions(_driver!);
            switch (action.Trim().ToLowerInvariant())
            {
                case "click":
                    actions.MoveToElement(el).Click().Perform();
                    break;
                case "doubleclick":
                    actions.MoveToElement(el).DoubleClick().Perform();
                    break;
                case "rightclick":
                    actions.MoveToElement(el).ContextClick().Perform();
                    break;
                case "hover":
                    actions.MoveToElement(el).Perform();
                    break;
                default:
                    throw new ArgumentException(
                        $"未知の action です: '{action}'. 'click' / 'doubleclick' / 'rightclick' / 'hover' のいずれかを指定してください。");
            }
            AutoAttachLocked();
        }
    }

    /// <summary>
    /// キーボードのキーを1つ押下する。現在フォーカスされている要素に対して送信される。
    /// </summary>
    /// <param name="key">
    /// キー名。OpenQA.Selenium.Keys のフィールド名（例: Enter, Tab, Escape, ArrowDown, Backspace）に
    /// 大文字小文字を区別せず一致すればそのキーコードを使用し、一致しなければ入力文字列としてそのまま送信する。
    /// </param>
    public void PressKey(string key)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var resolved = ResolveKey(key);
            new Actions(_driver!).SendKeys(resolved).Perform();
        }
    }

    /// <summary>
    /// キー名文字列を Selenium の Keys 定数、またはリテラル文字列へ解決する。
    /// </summary>
    /// <param name="key">キー名、または送信したい文字そのもの</param>
    /// <returns>SendKeys に渡すべき文字列</returns>
    private static string ResolveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key is empty.", nameof(key));

        var field = typeof(Keys).GetField(
            key,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);

        if (field != null && field.GetValue(null) is string mapped)
            return mapped;

        // 特殊キー名に一致しなければ、そのままの文字列（1文字含む）として送信する
        return key;
    }

    /// <summary>
    /// セレクタ一致要素にテキストを入力
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <param name="text">入力テキスト</param>
    /// <param name="clear">入力前にフィールドをクリアするか</param>
    public void InputText(string selector, SelectorType by, string text, bool clear)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var el = FindLocked(selector, by);
            if (clear) el.Clear();
            el.SendKeys(text);
        }
    }

    /// <summary>
    /// セレクタ一致要素の可視テキストを取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>要素の可視テキスト</returns>
    public string GetElementText(string selector, SelectorType by)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return FindLocked(selector, by).Text;
        }
    }

    /// <summary>
    /// セレクタ一致要素の属性値を取得
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <param name="attributeName">属性名</param>
    /// <returns>属性値。存在しない場合はnull</returns>
    public string? GetElementAttribute(string selector, SelectorType by, string attributeName)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            return FindLocked(selector, by).GetAttribute(attributeName);
        }
    }

    /// <summary>
    /// セレクタに一致する要素の数を取得します。
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>見つかった要素の数</returns>
    public int CountElements(string selector, SelectorType by)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            // 上で追加した FindAllLocked を利用
            return FindAllLocked(selector, by).Count;
        }
    }

    /// <summary>
    /// セレクタに一致するすべての要素の可視テキストを取得します。
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>各要素のテキストのリスト</returns>
    public List<string> GetMultipleElementsText(string selector, SelectorType by)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            // LINQを使って各要素のTextプロパティを抜き出す
            return FindAllLocked(selector, by).Select(el => el.Text).ToList();
        }
    }

    /// <summary>
    /// セレクタ一致要素の出現を待機
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <param name="timeoutSeconds">タイムアウト秒数</param>
    /// <returns>時間内に見つかったらtrue、タイムアウトならfalse</returns>
    public bool WaitForElement(string selector, SelectorType by, int timeoutSeconds)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var wait = new WebDriverWait(_driver!, TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            try
            {
                wait.Until(d => d.FindElements(ToSeleniumBy(selector, by)).Count > 0);
                return true;
            }
            catch (WebDriverTimeoutException)
            {
                return false;
            }
        }
    }

    // ---------- アラート / confirm / prompt ----------

    /// <summary>
    /// ブラウザの alert / confirm / prompt ダイアログを処理する。
    /// ダイアログはクリック等の操作直後に非同期で出現するため、出現をポーリング待機してから操作する。
    /// </summary>
    /// <param name="action">'accept' / 'dismiss' / 'get_text' / 'send_text'</param>
    /// <param name="text">'send_text' の場合に入力するテキスト</param>
    /// <param name="timeoutSeconds">ダイアログ出現待ちのタイムアウト秒数</param>
    /// <returns>action に応じた結果文字列</returns>
    public string HandleAlert(string action, string? text, int timeoutSeconds)
    {
        lock (_gate)
        {
            RequireStartedLocked();

            var wait = new WebDriverWait(_driver!, TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            IAlert alert;
            try
            {
                alert = wait.Until(d =>
                {
                    try { return d.SwitchTo().Alert(); }
                    catch (NoAlertPresentException) { return null; }
                });
            }
            catch (WebDriverTimeoutException)
            {
                throw new InvalidOperationException(
                    "指定時間内に alert/confirm/prompt が出現しませんでした。");
            }

            switch (action.Trim().ToLowerInvariant())
            {
                case "accept":
                    alert.Accept();
                    AutoAttachLocked();
                    return "accepted.";
                case "dismiss":
                    alert.Dismiss();
                    AutoAttachLocked();
                    return "dismissed.";
                case "get_text":
                    return alert.Text ?? string.Empty;
                case "send_text":
                    if (string.IsNullOrEmpty(text))
                        throw new ArgumentException("send_text には text の指定が必要です。", nameof(text));
                    alert.SendKeys(text);
                    return "text sent.";
                default:
                    throw new ArgumentException(
                        $"未知の action です: '{action}'. 'accept' / 'dismiss' / 'get_text' / 'send_text' のいずれかを指定してください。");
            }
        }
    }

    // ---------- スクリプト ----------

    /// <summary>
    /// 任意JavaScriptを現ページコンテキストで実行し結果を文字列で返す
    /// </summary>
    /// <param name="script">JSソース（return で値を返す）</param>
    /// <returns>戻り値。文字列以外はJSONシリアライズ</returns>
    public string ExecuteScript(string script)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var result = ((IJavaScriptExecutor)_driver!).ExecuteScript(script);
            AutoAttachLocked();
            return result switch
            {
                null     => "null",
                string s => s,
                _        => JsonConvert.SerializeObject(result),
            };
        }
    }

    // ---------- フレーム (iframe) ----------

    /// <summary>
    /// 現在のフレームコンテキスト内の iframe/frame を列挙する（JSON文字列）。
    /// </summary>
    public string ListFrames()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var js = (IJavaScriptExecutor)_driver!;
            const string script = @"
var frames = document.querySelectorAll('iframe, frame');
var out = [];
for (var i = 0; i < frames.length; i++) {
  var f = frames[i];
  var info = { index: i, id: f.id || '', name: f.name || '', src: f.src || '', sameOrigin: false, textLength: 0 };
  try {
    var doc = f.contentDocument || (f.contentWindow && f.contentWindow.document);
    if (doc) { info.sameOrigin = true; info.textLength = doc.body ? doc.body.innerText.length : 0; }
  } catch (e) { info.sameOrigin = false; }
  out.push(info);
}
return JSON.stringify(out, null, 2);";
            return js.ExecuteScript(script) as string ?? "[]";
        }
    }

    /// <summary>
    /// 指定した iframe/frame にコンテキストを切り替える。
    /// 以降の find/click/input/get_page_text 等はそのフレーム内を対象とする。
    /// navigate を行うと自動的にトップ文書へ戻る。
    /// </summary>
    public void SwitchToFrame(string selector, FrameTarget by)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            switch (by)
            {
                case FrameTarget.Index:
                    if (!int.TryParse(selector, out var idx))
                        throw new ArgumentException($"index として解釈できません: '{selector}'");
                    _driver!.SwitchTo().Frame(idx);
                    break;
                case FrameTarget.IdOrName:
                    _driver!.SwitchTo().Frame(selector);
                    break;
                case FrameTarget.Xpath:
                    _driver!.SwitchTo().Frame(_driver.FindElement(By.XPath(selector)));
                    break;
                case FrameTarget.Css:
                default:
                    _driver!.SwitchTo().Frame(_driver.FindElement(By.CssSelector(selector)));
                    break;
            }
        }
    }

    /// <summary>
    /// 一つ上の親フレームにコンテキストを戻す。
    /// </summary>
    public void SwitchToParentFrame()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            _driver!.SwitchTo().ParentFrame();
        }
    }

    /// <summary>
    /// トップ文書（default content）にコンテキストを戻す。
    /// </summary>
    public void SwitchToDefaultContent()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            _driver!.SwitchTo().DefaultContent();
        }
    }

    /// <summary>
    /// トップ文書から全 iframe/frame を実際に切り替えながら再帰的に可視テキストを収集する。
    /// WebDriver レベルで切り替えるためクロスオリジン iframe の内部テキストも取得できる。
    /// 収集後はトップ文書にコンテキストを戻す。
    /// </summary>
    public string GetAllText(int maxDepth)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var sb = new System.Text.StringBuilder();
            _driver!.SwitchTo().DefaultContent();
            try
            {
                CollectFrameTextLocked(sb, "TOP", Math.Max(1, maxDepth));
            }
            finally
            {
                try { _driver.SwitchTo().DefaultContent(); } catch { }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// GetAllText の再帰本体（ロック取得済み前提）。
    /// </summary>
    private void CollectFrameTextLocked(System.Text.StringBuilder sb, string path, int depth)
    {
        var js = (IJavaScriptExecutor)_driver!;

        string bodyText;
        try { bodyText = js.ExecuteScript("return document.body ? document.body.innerText : '';") as string ?? ""; }
        catch { bodyText = ""; }
        bodyText = bodyText.Trim();
        if (bodyText.Length > 0)
        {
            sb.Append("===== [").Append(path).Append("] =====").Append('\n');
            sb.Append(bodyText).Append('\n');
        }

        if (depth <= 0) return;

        int frameCount;
        try { frameCount = Convert.ToInt32(js.ExecuteScript("return window.frames.length;")); }
        catch { frameCount = 0; }
        if (frameCount == 0) return;

        var labels = new List<string>();
        try
        {
            var labelJson = js.ExecuteScript(@"
var fr = document.querySelectorAll('iframe, frame');
var out = [];
for (var i = 0; i < fr.length; i++) {
  var f = fr[i];
  out.push(f.id ? ('#'+f.id) : (f.name ? ('name='+f.name) : ''));
}
return JSON.stringify(out);") as string ?? "[]";
            labels = JsonConvert.DeserializeObject<List<string>>(labelJson) ?? new List<string>();
        }
        catch { }

        for (int i = 0; i < frameCount; i++)
        {
            var lbl = (i < labels.Count && !string.IsNullOrEmpty(labels[i])) ? labels[i] : $"[{i}]";
            var childPath = $"{path} > iframe{lbl}";
            try
            {
                _driver!.SwitchTo().Frame(i);
            }
            catch (Exception ex)
            {
                sb.Append("===== [").Append(childPath).Append("] (切替失敗: ")
                  .Append(ex.GetType().Name).Append(") =====").Append('\n');
                continue;
            }
            try
            {
                CollectFrameTextLocked(sb, childPath, depth - 1);
            }
            finally
            {
                try { _driver!.SwitchTo().ParentFrame(); } catch { }
            }
        }
    }

    // ---------- 要素スキャン（find_element / list_interactive_elements 共通基盤） ----------

    /// <summary>
    /// 要素スキャン結果の1件分。生成したセレクタはブラウザ上で検証済み
    /// （そのセレクタで引き直すと必ず1件だけ一致し、同一ノードである）。
    /// </summary>
    public sealed class ElementHit
    {
        /// <summary>検証済みセレクタ</summary>
        public string Selector { get; set; } = "";
        /// <summary>セレクタ種別（Css / Xpath）</summary>
        public string By { get; set; } = "";
        /// <summary>タグ名</summary>
        public string Tag { get; set; } = "";
        /// <summary>input の type 属性など</summary>
        public string Type { get; set; } = "";
        /// <summary>判別用ラベル（可視テキスト等）</summary>
        public string Label { get; set; } = "";
        /// <summary>visible / hidden / disabled などの状態</summary>
        public string State { get; set; } = "";
        /// <summary>マッチスコア</summary>
        public int Score { get; set; }
        /// <summary>所属フレームのパス（例: TOP &gt; iframe#app）</summary>
        public string FramePath { get; set; } = "TOP";
        /// <summary>そのフレームへ入る手順。TOP文書なら空文字列</summary>
        public string SwitchHint { get; set; } = "";
    }

    /// <summary>
    /// 要素走査＆セレクタ生成を行う JavaScript。
    /// arguments[0]=検索クエリ（空文字列ならインベントリモード）
    /// arguments[1]=操作可能要素のみに絞るか
    /// arguments[2]=返却上限
    /// 生成したセレクタは必ず querySelectorAll / document.evaluate で引き直して
    /// 「1件だけ一致し同一ノード」であることを確認してから返す。
    /// </summary>
    private const string ElementScanJs = @"
var q = arguments[0] || '';
var interactiveOnly = !!arguments[1];
var limit = arguments[2] || 20;

function norm(s){
  if(!s) return '';
  s = String(s);
  try { s = s.normalize('NFKC'); } catch(e){}
  return s.toLowerCase().replace(/\s+/g,' ').trim();
}
var nq = norm(q);

/* セレクタ組み立てで使う文字（C#の逐語文字列を汚さないため定数化） */
var qt = String.fromCharCode(39);
var chr61 = String.fromCharCode(61);

var INTERACTIVE = 'a,button,input,select,textarea,summary,label,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[role=textbox],[role=combobox],[role=switch],[onclick],[tabindex],[contenteditable=true]';

function isInteractive(el){ try { return el.matches(INTERACTIVE); } catch(e){ return false; } }

function isVisible(el){
  try {
    if(!el.getClientRects || el.getClientRects().length === 0) return false;
    var st = window.getComputedStyle(el);
    if(!st) return false;
    if(st.visibility === 'hidden' || st.display === 'none' || st.opacity === '0') return false;
    return true;
  } catch(e){ return false; }
}

function ownText(el){
  var t = '';
  for(var i=0;i<el.childNodes.length;i++){
    var n = el.childNodes[i];
    if(n.nodeType === 3) t += n.nodeValue;
  }
  return t.replace(/\s+/g,' ').trim();
}

/* 自動生成っぽい id を弾く（例: _96bgwl4m2zh-input, :r3:, ember1234） */
function looksGenerated(id){
  if(!id) return true;
  if(id.length > 40) return true;
  if(/^[:_]/.test(id)) return true;
  if(/^r[0-9a-z]{4,}$/i.test(id)) return true;
  var segs = id.split(/[-_.:]/);
  for(var i=0;i<segs.length;i++){
    var s = segs[i];
    if(s.length >= 8 && /[a-z]/i.test(s) && /\d/.test(s)) return true;
    if(/\d{5,}/.test(s)) return true;
  }
  return false;
}

function cssEsc(v){
  try { return CSS.escape(v); } catch(e){ return String(v); }
}
function attrEsc(v){ return String(v).replace(/(['\\])/g, '\\$1'); }

function uniqCss(sel, el){
  try { var n = document.querySelectorAll(sel); return n.length === 1 && n[0] === el; }
  catch(e){ return false; }
}
function uniqXp(xp, el){
  try {
    /* 7 = ORDERED_NODE_SNAPSHOT_TYPE。9(FIRST_ORDERED_NODE)だと先頭しか見ず重複を検出できない */
    var r = document.evaluate(xp, document, null, 7, null);
    return r.snapshotLength === 1 && r.snapshotItem(0) === el;
  }
  catch(e){ return false; }
}

function xpathOf(el){
  var parts = [], cur = el;
  while(cur && cur.nodeType === 1){
    var idx = 1, sib = cur.previousElementSibling;
    while(sib){ if(sib.tagName === cur.tagName) idx++; sib = sib.previousElementSibling; }
    parts.unshift(cur.tagName.toLowerCase() + '[' + idx + ']');
    cur = cur.parentElement;
  }
  return '/' + parts.join('/');
}

/* 安定 id を持つ最近傍の祖先を起点にした相対 XPath。絶対パスより短く壊れにくい */
function relXpathOf(el){
  var parts = [], cur = el;
  while(cur && cur.nodeType === 1){
    var pid = cur.getAttribute('id');
    if(pid && cur !== el && !looksGenerated(pid) && pid.indexOf(qt) < 0){
      return '//*[@id=' + qt + pid + qt + ']/' + parts.join('/');
    }
    var idx = 1, sib = cur.previousElementSibling;
    while(sib){ if(sib.tagName === cur.tagName) idx++; sib = sib.previousElementSibling; }
    parts.unshift(cur.tagName.toLowerCase() + '[' + idx + ']');
    cur = cur.parentElement;
  }
  return null;
}

/* 安定性の高い順にセレクタ候補を試し、検証を通った最初のものを返す */
function buildSelector(el){
  var tag = el.tagName.toLowerCase(), c, v;

  var id = el.getAttribute('id');
  if(id && !looksGenerated(id)){ c = '#' + cssEsc(id); if(uniqCss(c, el)) return {s:c, by:'Css'}; }

  var tids = ['data-testid','data-test-id','data-test','data-cy','data-qa'];
  for(var i=0;i<tids.length;i++){
    v = el.getAttribute(tids[i]);
    if(v){ c = '[' + tids[i] + chr61 + qt + attrEsc(v) + qt + ']'; if(uniqCss(c, el)) return {s:c, by:'Css'}; }
  }

  v = el.getAttribute('aria-label');
  if(v){ c = tag + '[aria-label=' + qt + attrEsc(v) + qt + ']'; if(uniqCss(c, el)) return {s:c, by:'Css'}; }

  v = el.getAttribute('name');
  if(v){ c = tag + '[name=' + qt + attrEsc(v) + qt + ']'; if(uniqCss(c, el)) return {s:c, by:'Css'}; }

  v = el.getAttribute('placeholder');
  if(v){ c = tag + '[placeholder=' + qt + attrEsc(v) + qt + ']'; if(uniqCss(c, el)) return {s:c, by:'Css'}; }

  if(tag === 'a'){
    v = el.getAttribute('href');
    if(v && v.length <= 120){
      c = 'a[href=' + qt + attrEsc(v) + qt + ']';
      if(uniqCss(c, el)) return {s:c, by:'Css'};
    }
  }

  /* まず直下テキストで試す。子孫に件数など可変の文言があっても巻き込まない */
  var ot = ownText(el);
  if(ot && ot.length <= 40 && ot.indexOf(qt) < 0){
    var xp0 = '//' + tag + '[normalize-space(text())=' + qt + ot + qt + ']';
    if(uniqXp(xp0, el)) return {s:xp0, by:'Xpath'};
  }

  /* 次に全テキスト。述語が normalize-space() なので比較元も全テキストで揃える */
  var ft = (el.textContent || '').replace(/\s+/g,' ').trim();
  if(ft && ft.length <= 40 && ft.indexOf(qt) < 0){
    var xp = '//' + tag + '[normalize-space()=' + qt + ft + qt + ']';
    if(uniqXp(xp, el)) return {s:xp, by:'Xpath'};
  }

  if(id){ c = '#' + cssEsc(id); if(uniqCss(c, el)) return {s:c, by:'Css'}; }

  var rel = relXpathOf(el);
  if(rel && uniqXp(rel, el)) return {s:rel, by:'Xpath'};

  var xp2 = xpathOf(el);
  if(uniqXp(xp2, el)) return {s:xp2, by:'Xpath'};

  return null;
}

function scoreEl(el){
  var fields = [];
  function add(v, w){ if(v){ var n = norm(v); if(n) fields.push([n, w]); } }
  add(ownText(el), 1.0);
  add(el.getAttribute('aria-label'), 1.0);
  add(el.getAttribute('placeholder'), 0.95);
  add(el.getAttribute('title'), 0.9);
  add(el.getAttribute('alt'), 0.9);
  add(el.getAttribute('value'), 0.85);
  add(el.getAttribute('name'), 0.7);
  add(el.getAttribute('id'), 0.6);
  add(el.getAttribute('role'), 0.5);
  var tc = (el.textContent || '').replace(/\s+/g,' ').trim();
  if(tc && tc.length <= 80) add(tc, 0.8);

  var best = 0;
  for(var i=0;i<fields.length;i++){
    var fv = fields[i][0], fw = fields[i][1], sc = 0;
    if(fv === nq) sc = 100;
    else if(fv.indexOf(nq) === 0) sc = 80;
    else if(fv.indexOf(nq) >= 0) sc = 60;
    else continue;
    sc = sc * fw - Math.min(20, Math.max(0, fv.length - nq.length) / 5);
    if(sc > best) best = sc;
  }
  return best;
}

var pool = interactiveOnly ? document.querySelectorAll(INTERACTIVE) : document.querySelectorAll('*');
var SKIP = {script:1, style:1, meta:1, link:1, head:1, html:1, body:1, noscript:1, br:1, iframe:1, frame:1};
var cands = [];
for(var i=0;i<pool.length;i++){
  var el = pool[i];
  var tg = el.tagName.toLowerCase();
  if(SKIP[tg]) continue;
  var vis = isVisible(el);
  var inter = isInteractive(el);
  var sc;
  if(nq === ''){
    if(!vis) continue;
    sc = inter ? 10 : 0;
  } else {
    sc = scoreEl(el);
    if(sc <= 0) continue;
    if(inter) sc += 40; else sc -= 20;
    sc += vis ? 10 : -30;
    try { if(el.disabled) sc -= 10; } catch(e){}
    if(!inter && el.children.length > 3) sc -= 25;
  }
  cands.push([el, sc, vis, inter]);
}
cands.sort(function(a,b){ return b[1] - a[1]; });

var res = [];
for(var j=0;j<cands.length && res.length<limit;j++){
  var e = cands[j][0];
  var sel = buildSelector(e);
  if(!sel) continue;
  var lab = ownText(e) || e.getAttribute('aria-label') || e.getAttribute('placeholder')
            || e.getAttribute('value') || e.getAttribute('title') || e.getAttribute('alt')
            || (e.textContent || '').replace(/\s+/g,' ').trim();
  lab = (lab || '').replace(/\s+/g,' ').trim();
  if(lab.length > 60) lab = lab.substring(0,60) + '...';
  var st = [cands[j][2] ? 'visible' : 'hidden'];
  try { if(e.disabled) st.push('disabled'); } catch(ex){}
  if(!cands[j][3]) st.push('non-interactive');
  res.push({
    Selector: sel.s, By: sel.by, Tag: e.tagName.toLowerCase(),
    Type: e.getAttribute('type') || '', Label: lab,
    State: st.join(','), Score: Math.round(cands[j][1])
  });
}
return JSON.stringify(res);
";

    /// <summary>
    /// 要素を走査して、検証済みセレクタ付きの候補一覧を返す。
    /// </summary>
    /// <param name="query">検索クエリ。空文字列ならインベントリモード（操作可能要素の一覧）</param>
    /// <param name="maxResults">返却上限</param>
    /// <param name="includeFrames">iframe 内も再帰的に走査するか</param>
    /// <param name="interactiveOnly">操作可能要素のみに絞るか</param>
    /// <returns>スコア降順の候補リスト</returns>
    public List<ElementHit> ScanElements(string? query, int maxResults, bool includeFrames, bool interactiveOnly)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var acc = new List<ElementHit>();
            _driver!.SwitchTo().DefaultContent();
            try
            {
                ScanFrameLocked(query ?? "", maxResults, interactiveOnly, "TOP", "", includeFrames ? 5 : 0, acc);
            }
            finally
            {
                try { _driver.SwitchTo().DefaultContent(); } catch { }
            }
            return acc.OrderByDescending(h => h.Score).Take(maxResults).ToList();
        }
    }

    /// <summary>
    /// ScanElements の再帰本体（ロック取得済み前提）。
    /// GetAllText と同じく WebDriver レベルでフレームを切り替えるため、
    /// クロスオリジン iframe の内部も走査できる。
    /// </summary>
    private void ScanFrameLocked(string query, int maxResults, bool interactiveOnly,
                                 string path, string switchHint, int depth, List<ElementHit> acc)
    {
        var js = (IJavaScriptExecutor)_driver!;

        try
        {
            var json = js.ExecuteScript(ElementScanJs, query, interactiveOnly, maxResults) as string ?? "[]";
            var hits = JsonConvert.DeserializeObject<List<ElementHit>>(json) ?? new List<ElementHit>();
            foreach (var h in hits)
            {
                h.FramePath = path;
                h.SwitchHint = switchHint;
                acc.Add(h);
            }
        }
        catch { /* このフレームは走査不能。スキップして続行 */ }

        if (depth <= 0) return;

        int frameCount;
        try { frameCount = Convert.ToInt32(js.ExecuteScript("return window.frames.length;")); }
        catch { frameCount = 0; }
        if (frameCount == 0) return;

        var labels = new List<string>();
        try
        {
            var labelJson = js.ExecuteScript(@"
var fr = document.querySelectorAll('iframe, frame');
var out = [];
for (var i = 0; i < fr.length; i++) {
  var f = fr[i];
  out.push(f.id ? ('#'+f.id) : (f.name ? ('name='+f.name) : ''));
}
return JSON.stringify(out);") as string ?? "[]";
            labels = JsonConvert.DeserializeObject<List<string>>(labelJson) ?? new List<string>();
        }
        catch { }

        for (int i = 0; i < frameCount; i++)
        {
            var lbl = (i < labels.Count && !string.IsNullOrEmpty(labels[i])) ? labels[i] : $"[{i}]";
            var childPath = $"{path} > iframe{lbl}";
            var childHint = string.IsNullOrEmpty(switchHint)
                ? $"switch_to_frame('{i}')"
                : $"{switchHint} -> switch_to_frame('{i}')";

            try { _driver!.SwitchTo().Frame(i); }
            catch { continue; }

            try { ScanFrameLocked(query, maxResults, interactiveOnly, childPath, childHint, depth - 1, acc); }
            finally { try { _driver!.SwitchTo().ParentFrame(); } catch { } }
        }
    }
    // ---------- ダウンロード ----------

    /// <summary>
    /// ダウンロードフォルダを変更する。
    /// Browser 起動中は CDP 経由でリアルタイム変更。Firefox は起動時にのみ適用。未起動なら次回起動時に適用。
    /// </summary>
    /// <param name="path">絶対パスのフォルダ</param>
    public void SetDownloadDir(string path)
    {
        lock (_gate)
        {
            _downloadDir = Path.GetFullPath(path);

            // フォルダが存在しない場合は作成
            if (!Directory.Exists(_downloadDir))
                Directory.CreateDirectory(_downloadDir);

            // Chrome 起動中なら CDP でリアルタイム変更
            if (_driver is ChromeDriver chromeDriver)
            {
                chromeDriver.ExecuteCdpCommand(
                    "Browser.setDownloadBehavior",
                    new Dictionary<string, object?>
                    {
                        { "behavior", "allow" },
                        { "downloadPath", _downloadDir },
                        { "eventsEnabled", false },
                    });
            }
        }
    }

    /// <summary>
    /// 現在設定されているダウンロードフォルダを返す
    /// </summary>
    /// <returns>フォルダパス。未設定の場合は空文字列</returns>
    public string GetDownloadDir()
    {
        lock (_gate) { return _downloadDir; }
    }

    /// <summary>
    /// ダウンロードフォルダ内のファイル一覧を返す（ダウンロード中の .crdownload を除外）
    /// </summary>
    /// <returns>ファイル名のリスト</returns>
    public List<string> ListDownloads()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_downloadDir) || !Directory.Exists(_downloadDir))
                return [];

            return [.. Directory.GetFiles(_downloadDir)
                .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    return $"{info.Name}  ({info.Length:N0} bytes)  {info.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
                })
                .OrderByDescending(s => s)];
        }
    }

    /// <summary>
    /// 指定パターンのファイルがダウンロード完了するまで待機する。
    /// .crdownload の消滅とファイルサイズの安定化で完了を判定する。
    /// </summary>
    /// <param name="pattern">検索パターン（例: "*.pdf", "report_*.xlsx"）</param>
    /// <param name="timeoutSeconds">タイムアウト秒数</param>
    /// <returns>完了したファイルのフルパス。タイムアウトなら null</returns>
    public string? WaitForDownload(string pattern, int timeoutSeconds)
    {
        if (string.IsNullOrEmpty(_downloadDir))
            throw new InvalidOperationException(
                "Download directory is not configured. Call 'set_download_dir' first.");

        var deadline = DateTime.Now.AddSeconds(Math.Max(1, timeoutSeconds));

        while (DateTime.Now < deadline)
        {
            // 進行中（.crdownload）がなくなるまで待つ
            var inProgress = Directory.GetFiles(_downloadDir, "*.crdownload");
            if (inProgress.Length == 0)
            {
                var files = Directory.GetFiles(_downloadDir, pattern)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .ToArray();

                if (files.Length > 0)
                {
                    // ファイルサイズが 2 回連続で一致したら安定とみなす
                    var file = files[0];
                    var size1 = new FileInfo(file).Length;
                    Thread.Sleep(600);
                    var size2 = new FileInfo(file).Length;
                    if (size1 == size2 && size1 > 0)
                        return file;
                }
            }

            Thread.Sleep(500);
        }

        return null;
    }

    // ---------- Window / Tab 管理 ----------

    /// <summary>
    /// 現在開いている全 window/tab の情報を返す。
    /// Title/Url を取得するため一時的に各 window へ切替えるが、最後に元の window に戻す。
    /// </summary>
    /// <returns>WindowInfo の一覧</returns>
    public IReadOnlyList<WindowInfo> ListWindows()
    {
        lock (_gate)
        {
            RequireStartedLocked();

            string? originalCurrent = null;
            try { originalCurrent = _driver!.CurrentWindowHandle; } catch { /* current が消えていることがある */ }

            var result = new List<WindowInfo>();
            foreach (var h in _driver!.WindowHandles)
            {
                _driver.SwitchTo().Window(h);
                result.Add(new WindowInfo(h, _driver.Title, _driver.Url, h == originalCurrent));
            }

            // 元の window に戻す（消えていれば最後の window に残す）
            if (originalCurrent != null && _driver.WindowHandles.Contains(originalCurrent))
            {
                _driver.SwitchTo().Window(originalCurrent);
            }

            return result;
        }
    }

    /// <summary>
    /// 指定の handle の window に切替える。
    /// </summary>
    /// <param name="handle">ListWindows で得た window ハンドル</param>
    public void SwitchToWindow(string handle)
    {
        lock (_gate)
        {
            RequireStartedLocked();
            if (!_driver!.WindowHandles.Contains(handle))
                throw new InvalidOperationException($"window handle not found: {handle}");
            _driver.SwitchTo().Window(handle);
            // 切替えても _previousHandles は変更しない（window 集合は変わっていない）
        }
    }

    /// <summary>
    /// 現在の window を閉じて、残った window のうち最後のものに切替える。
    /// 最後の window を閉じるとブラウザ全体が終了するため、その場合は例外。
    /// </summary>
    public void CloseCurrentWindow()
    {
        lock (_gate)
        {
            RequireStartedLocked();
            var handles = _driver!.WindowHandles;
            if (handles.Count <= 1)
                throw new InvalidOperationException(
                    "Cannot close the last remaining window. Use 'close_browser' to terminate the browser.");

            var current = _driver.CurrentWindowHandle;
            _driver.Close();

            var remaining = _driver.WindowHandles.Where(h => h != current).ToList();
            if (remaining.Count > 0)
                _driver.SwitchTo().Window(remaining[^1]);

            _previousHandles = _driver.WindowHandles.ToArray();
        }
    }

    // ---------- 内部ヘルパ ----------

    /// <summary>
    /// ドライバを破棄してセッションをリセットする（ロック取得済み前提）。
    /// Browser が予期せず終了した場合の復旧に使用する。
    /// </summary>
    private void ResetSessionLocked()
    {
        try { _driver?.Dispose(); } catch { /* best-effort */ }
        _driver = null;
        _wait = null;
        _previousHandles = Array.Empty<string>();
        ReleaseProfileLockLocked();
    }

    /// <summary>
    /// 操作直後に呼び出す自動アタッチ。
    /// - 新しい window が増えていれば、その最新の window に切替える
    /// - 現在の window が閉じられていれば、残ったどれかに切替える
    /// 比較は <see cref="_previousHandles"/> との差分で行い、最後にスナップショットを更新する。
    /// </summary>
    private void AutoAttachLocked()
    {
        if (_driver == null) return;

        string[] currentHandles;
        try { currentHandles = _driver.WindowHandles.ToArray(); }
        catch { return; /* セッション死亡時は何もしない */ }

        // 新規 window を検出
        var newHandles = currentHandles.Except(_previousHandles).ToArray();

        // 現在のフォーカスが生きているか確認
        string? currentFocus = null;
        try { currentFocus = _driver.CurrentWindowHandle; } catch { /* 閉じられている */ }

        if (newHandles.Length > 0)
        {
            // 新 window があれば最後（通常は最新）に切替え
            _driver.SwitchTo().Window(newHandles[^1]);
            Logger.Log($"Auto-attached to new window: {newHandles[^1]}", LogType.Operation);
            try { WaitReadyLocked(); } catch { /* ロード途中でも気にしない */ }
        }
        else if (currentFocus == null && currentHandles.Length > 0)
        {
            // 現 window が閉じられた → 残った最後の window に切替え
            _driver.SwitchTo().Window(currentHandles[^1]);
        }

        _previousHandles = currentHandles;
    }

    /// <summary>
    /// ロック内で要素を検索する
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>見つかった要素</returns>
    private IWebElement FindLocked(string selector, SelectorType by)
        => _driver!.FindElement(ToSeleniumBy(selector, by));

    /// <summary>
    /// ロック内で複数の要素を検索する
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>見つかった要素のリスト</returns>
    private ReadOnlyCollection<IWebElement> FindAllLocked(string selector, SelectorType by)
        => _driver!.FindElements(ToSeleniumBy(selector, by));

    /// <summary>
    /// SelectorType から Selenium の By インスタンスに変換
    /// </summary>
    /// <param name="selector">セレクタ文字列</param>
    /// <param name="by">セレクタ種別</param>
    /// <returns>Selenium By</returns>
    private static By ToSeleniumBy(string selector, SelectorType by) => by switch
    {
        SelectorType.Xpath => By.XPath(selector),
        _                  => By.CssSelector(selector),
    };

    /// <summary>
    /// ブラウザ未起動なら例外を投げる（ロック取得済み前提）
    /// </summary>
    private void RequireStartedLocked()
    {
        if (_driver == null)
            throw new InvalidOperationException("Browser is not started. Call 'navigate' first.");
    }

    /// <summary>
    /// document.readyState が complete になるまで待機（ロック取得済み前提）
    /// </summary>
    private void WaitReadyLocked()
    {
        _wait!.Until(d =>
        {
            var state = ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState") as string;
            return state == "complete";
        });
    }

    /// <summary>
    /// ブラウザ未起動なら起動する（ロック取得済み前提）
    /// </summary>
    private void EnsureStartedLocked()
    {
        if (_driver != null) return;

        Logger.Log($"EnsureStartedLocked", LogType.System);

        try
        {
            var name = _selectedName ?? "default";
            _selectedName = name;

            var info = ResolveInfo(name);

            if (!File.Exists(info.Browser))
            {
                Logger.Log($"{info.BrowserType} 実行体が見つからない: {info.Browser}", LogType.System);
                throw new FileNotFoundException($"{info.BrowserType} 実行体が見つからない: {info.Browser}");
            }
            if (!File.Exists(info.WebDriver))
            {
                Logger.Log($"WebDriver 実行体が見つからない: {info.WebDriver}", LogType.System);
                throw new FileNotFoundException($"WebDriver 実行体が見つからない: {info.WebDriver}");
            }

            // ダウンロードフォルダの決定（webinfo.json の値 → 既に SetDownloadDir 済みなら維持）
            if (string.IsNullOrEmpty(_downloadDir) && !string.IsNullOrEmpty(info.Download))
                _downloadDir = info.Download;

            // 永続プロファイル利用時はプロセス間で排他ロックを取得（同時操作を防止）
            AcquireProfileLockLocked(ExtractProfilePath(info));

            switch (info.BrowserType.ToLowerInvariant())
            {
                case "firefox":
                    BuildFirefoxBrowser(info);
                    break;
                case "chrome":
                default:
                    BuildChromeBrowser(info);
                    break;
            }


            // 自動アタッチ用の初期スナップショット
            _previousHandles = _driver!.WindowHandles.ToArray();
        }
        catch (Exception ex)
        {
            // 起動失敗時はプロファイルロックを手放す
            ReleaseProfileLockLocked();
            Logger.Log($"Failed to start browser: {ex.Message}", LogType.System);
            throw;
        }
    }

    /// <summary>
    /// Chrome用
    /// </summary>
    /// <param name="info"></param>
    private void BuildChromeBrowser( WebdriverInfo info )
    {
        var service = ChromeDriverService.CreateDefaultService(
            Path.GetDirectoryName(info.WebDriver)!,
            Path.GetFileName(info.WebDriver));

        var options = new ChromeOptions { BinaryLocation = info.Browser };
        info.Args.ForEach(a => options.AddArgument(a));

        // ダウンロードフォルダが設定されていれば Chrome 起動時に適用
        if (!string.IsNullOrEmpty(_downloadDir))
        {
            if (!Directory.Exists(_downloadDir))
                Directory.CreateDirectory(_downloadDir);

            options.AddUserProfilePreference("download.default_directory", _downloadDir);
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("download.directory_upgrade", true);
            options.AddUserProfilePreference("safebrowsing.enabled", false);
        }

        _driver = new ChromeDriver(service, options);
        _wait = new WebDriverWait(_driver, TimeSpan.FromMinutes(2));
        Logger.Log($"Chrome started. chrome={info.Browser}", LogType.System);

        // AddUserProfilePreference より既存プロファイルの保存値が優先されるため、
        // 起動直後に CDP で強制上書きする。
        if (!string.IsNullOrEmpty(_downloadDir) && _driver is ChromeDriver chromeForDl)
        {
            chromeForDl.ExecuteCdpCommand(
                "Browser.setDownloadBehavior",
                new Dictionary<string, object?>
                {
                    { "behavior", "allow" },
                    { "downloadPath", _downloadDir },
                    { "eventsEnabled", false },
                });
        }
    }

    /// <summary>
    /// Firefox用
    /// </summary>
    /// <param name="info"></param>
    private void BuildFirefoxBrowser( WebdriverInfo info )
    {
        var service = FirefoxDriverService.CreateDefaultService(
            Path.GetDirectoryName(info.WebDriver)!,
            Path.GetFileName(info.WebDriver));

        var options = new FirefoxOptions { BinaryLocation = info.Browser };
        info.Args.ForEach(a => options.AddArgument(a));

        if (!string.IsNullOrEmpty(_downloadDir))
        {
            if (!Directory.Exists(_downloadDir))
                Directory.CreateDirectory(_downloadDir);

            options.SetPreference("browser.download.dir", _downloadDir);
            options.SetPreference("browser.download.folderList", 2);
            options.SetPreference("browser.helperApps.neverAsk.saveToDisk", "");
            options.SetPreference("browser.download.manager.showWhenStarting", false);
        }

        _driver = new FirefoxDriver(service, options);
        _wait = new WebDriverWait(_driver, TimeSpan.FromMinutes(2));
        Logger.Log($"Firefox started. firefox={info.Browser}", LogType.System);
    }

    /// <summary>
    /// 選択名に対応するブラウザ定義を解決する。存在しなければ例外。
    /// </summary>
    private static WebdriverInfo ResolveInfo(string name)
    {
        var all = LoadAllBrowsers();
        if (all.Count == 0)
            throw new InvalidOperationException("webdriverinfo.json が見つからない、または解析できません。");
        if (all.TryGetValue(name, out var info))
            return info;
        throw new InvalidOperationException(
            $"ブラウザ定義 '{name}' が存在しません。利用可能: {string.Join(", ", all.Keys)}");
    }

    /// <summary>
    /// 設定ファイルを読み込み、名前付きブラウザ定義の辞書を返す。
    /// 新形式 {"Browsers":{"name":{...}}} と、旧形式（フラット単一定義=default）の両対応。
    /// </summary>
    private static Dictionary<string, WebdriverInfo> LoadAllBrowsers()
    {
        Logger.Log($"LoadAllBrowsers", LogType.System);

        var fullpath = !string.IsNullOrEmpty(webdriverinfopath)
            ? webdriverinfopath
            : Path.Combine(AppContext.BaseDirectory, "webdriverinfo.json");

        if (!File.Exists(fullpath))
            return new(StringComparer.OrdinalIgnoreCase);

        var text = File.ReadAllText(fullpath);
        var root = JObject.Parse(text);

        // 新形式: 名前付き複数定義
        if (root["Browsers"] != null)
        {
            var cfg = root.ToObject<WebdriverConfig>();
            var dict = new Dictionary<string, WebdriverInfo>(StringComparer.OrdinalIgnoreCase);
            if (cfg?.Browsers != null)
                foreach (var kv in cfg.Browsers)
                    dict[kv.Key] = kv.Value;
            return dict;
        }

        // 旧形式: フラット単一定義 → "default" として後方互換
        var flat = root.ToObject<WebdriverInfo>();
        var single = new Dictionary<string, WebdriverInfo>(StringComparer.OrdinalIgnoreCase);
        if (flat != null) single["default"] = flat;
        return single;
    }

    /// <summary>
    /// ブラウザ定義から永続プロファイルのパスを抽出する。指定が無ければ null（一時プロファイル）。
    /// Chrome: --user-data-dir=PATH / Firefox: -profile PATH または -profile=PATH
    /// </summary>
    private static string? ExtractProfilePath(WebdriverInfo info)
    {
        var args = info.Args;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(a["--user-data-dir=".Length..]);
            if (a.StartsWith("-profile=", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(a["-profile=".Length..]);
            if (string.Equals(a, "-profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return Path.GetFullPath(args[i + 1]);
        }
        return null;
    }

    /// <summary>
    /// 永続プロファイルの排他ロックを取得する（ロック取得済み前提）。
    /// プロファイル配下のロックファイルを FileShare.None で掴み、プロセス寿命の間保持する。
    /// 他プロセスが同じプロファイルを掴んでいれば例外。
    /// </summary>
    private void AcquireProfileLockLocked(string? profilePath)
    {
        if (string.IsNullOrEmpty(profilePath))
            return;  // 一時プロファイルは排他不要

        Directory.CreateDirectory(profilePath);
        var lockFile = Path.Combine(profilePath, ".seleniumsvr.lock");
        try
        {
            _profileLock = new FileStream(
                lockFile, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"プロファイル '{profilePath}' は別のセッションが使用中です。" +
                "同一プロファイルの同時操作はできません。既存セッションの終了後に再実行してください。");
        }
    }

    /// <summary>
    /// プロファイルの排他ロックを解放する（ロック取得済み前提）。
    /// </summary>
    private void ReleaseProfileLockLocked()
    {
        try { _profileLock?.Dispose(); } catch { /* best-effort */ }
        _profileLock = null;
    }

    /// <summary>
    /// 破棄処理。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try { _driver?.Quit(); } catch { /* best-effort */ }
            _driver?.Dispose();
            _driver = null;
            _wait = null;
            ReleaseProfileLockLocked();
        }
    }
}
