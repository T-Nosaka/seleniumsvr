using Newtonsoft.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

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
/// ブラウザの window/tab の情報。
/// </summary>
/// <param name="Handle">Selenium 内部の window ハンドル（switch_window で指定）</param>
/// <param name="Title">window のタイトル</param>
/// <param name="Url">window の現在URL</param>
/// <param name="IsCurrent">現在アクティブな window か</param>
public sealed record WindowInfo(string Handle, string Title, string Url, bool IsCurrent);

/// <summary>
/// Chrome / WebDriver のセッションをプロセス寿命で管理するシングルトン。
/// - 初回 Navigate 呼び出しで Chrome を起動（遅延初期化）
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

    // ---------- ライフサイクル / ナビゲーション ----------

    /// <summary>
    /// 指定URLへ遷移。ブラウザ未起動なら自動で起動する。
    /// Chrome ウィンドウが手動で閉じられるなどセッションが死亡した場合は
    /// ドライバをリセットして Chrome を再起動し、自動復旧する。
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
                // ドライバをリセットして Chrome を再起動してリトライ
                Logger.Log($"Session lost, restarting Chrome. ({ex.Message})", LogType.System);
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
    /// ブラウザを終了してセッションをリセットする
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            try { _driver?.Quit(); } catch { /* best-effort */ }
            _driver?.Dispose();
            _driver = null;
            _wait = null;
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

    // ---------- ダウンロード ----------

    /// <summary>
    /// ダウンロードフォルダを変更する。
    /// Chrome 起動中は CDP 経由でリアルタイム変更。未起動なら次回起動時に適用。
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
    /// Chrome が予期せず終了した場合の復旧に使用する。
    /// </summary>
    private void ResetSessionLocked()
    {
        try { _driver?.Dispose(); } catch { /* best-effort */ }
        _driver = null;
        _wait = null;
        _previousHandles = Array.Empty<string>();
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
            var info = LoadWebdriverInfo()
                ?? throw new InvalidOperationException("webinfo.json が見つからない、または解析できません。");

            if (!File.Exists(info.Chrome))
            {
                Logger.Log($"Chrome 実行体が見つからない: {info.Chrome}", LogType.System);
                throw new FileNotFoundException($"Chrome 実行体が見つからない: {info.Chrome}");
            }
            if (!File.Exists(info.WebDriver))
            {
                Logger.Log($"WebDriver 実行体が見つからない: {info.WebDriver}", LogType.System);
                throw new FileNotFoundException($"WebDriver 実行体が見つからない: {info.WebDriver}");
            }

            // ダウンロードフォルダの決定（webinfo.json の値 → 既に SetDownloadDir 済みなら維持）
            if (string.IsNullOrEmpty(_downloadDir) && !string.IsNullOrEmpty(info.Download))
                _downloadDir = info.Download;

            var service = ChromeDriverService.CreateDefaultService(
                Path.GetDirectoryName(info.WebDriver)!,
                Path.GetFileName(info.WebDriver));

            var options = new ChromeOptions { BinaryLocation = info.Chrome };
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
            Logger.Log($"Chrome started. chrome={info.Chrome}", LogType.System);

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

            // 自動アタッチ用の初期スナップショット
            _previousHandles = _driver.WindowHandles.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start Chrome: {ex.Message}", LogType.System);
            throw;
        }
    }

    /// <summary>
    /// 自アセンブリと同じフォルダの webdriverinfo.json を読み込む。
    /// </summary>
    /// <returns>ChromeInfo。ファイルがなければnull</returns>
    private static WebdriverInfo? LoadWebdriverInfo()
    {
        Logger.Log($"LoadWebdriverInfo", LogType.System);

        var fullpath = !string.IsNullOrEmpty(webdriverinfopath)
            ? webdriverinfopath
            : Path.Combine(AppContext.BaseDirectory, "webdriverinfo.json");

        if (!File.Exists(fullpath)) return null;
        var text = File.ReadAllText(fullpath);

        return JsonConvert.DeserializeObject<WebdriverInfo>(text);
    }

    /// <summary>
    /// 破棄処理。ChromeDriverを確実にQuitする
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try { _driver?.Quit(); } catch { /* best-effort */ }
            _driver?.Dispose();
            _driver = null;
            _wait = null;
        }
    }
}
