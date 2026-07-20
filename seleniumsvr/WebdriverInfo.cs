
namespace seleniumsvr
{
    /// <summary>
    /// WebDriver情報
    /// </summary>
    public class WebdriverInfo
    {
        /// <summary>
        /// ブラウザ種別 ("chrome" or "firefox")
        /// </summary>
        public string BrowserType = "chrome";

        /// <summary>
        /// Browser実行体
        /// </summary>
        public string Browser = @"C:\webdriver\chrome-win64\chrome.exe";

        /// <summary>
        /// WebDriver実行体
        /// </summary>
        public string WebDriver = @"C:\webdriver\chromedriver-win64\chromedriver.exe";

        /// <summary>
        /// ダウンロードフォルダ
        /// </summary>
        public string Download = @"C:\webdriver\download";

        /// <summary>
        /// オプション引数
        /// </summary>
        public List<string> Args = new List<string>();
    }
}
