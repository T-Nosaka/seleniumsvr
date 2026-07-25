using System.Runtime.InteropServices;

namespace seleniumsvr
{
    /// <summary>
    /// WebDriver情報（1ブラウザ定義）
    /// </summary>
    public class WebdriverInfo
    {
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// ブラウザ種別 ("chrome" or "firefox")
        /// </summary>
        public string BrowserType = "chrome";

        /// <summary>
        /// Browser実行体
        /// </summary>
        public string Browser = IsMacOS
            ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
            : IsLinux
                ? "/usr/bin/google-chrome"
                : @"C:\webdriver\chrome-win64\chrome.exe";

        /// <summary>
        /// WebDriver実行体
        /// </summary>
        public string WebDriver = IsMacOS
            ? "/usr/local/bin/chromedriver"
            : IsLinux
                ? "/usr/bin/chromedriver"
                : @"C:\webdriver\chromedriver-win64\chromedriver.exe";

        /// <summary>
        /// ダウンロードフォルダ
        /// </summary>
        public string Download = IsMacOS
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : IsLinux
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : @"C:\webdriver\download";

        /// <summary>
        /// オプション引数
        /// </summary>
        public List<string> Args = new List<string>();
    }

    /// <summary>
    /// 設定ファイル(新形式)のルート。
    /// 名前付きで複数のブラウザ定義を保持する。
    /// 例:
    /// {
    ///   "Browsers": {
    ///     "default":  { "BrowserType": "chrome", ... },
    ///     "shopping": { "BrowserType": "chrome", ... }
    ///   }
    /// }
    /// ※ "Browsers" を持たない旧来のフラット形式は "default" 単一定義として後方互換で読み込まれる。
    /// </summary>
    public class WebdriverConfig
    {
        /// <summary>
        /// 定義名 → ブラウザ定義 のマップ
        /// </summary>
        public Dictionary<string, WebdriverInfo>? Browsers { get; set; }
    }
}
