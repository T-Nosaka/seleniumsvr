
namespace seleniumsvr
{
    /// <summary>
    /// WebDriver情報
    /// </summary>
    public class WebdriverInfo
    {
        /// <summary>
        /// Chrome実行体
        /// </summary>
        public string Chrome = @"C:\webdriver\chrome-win64\chrome.exe";

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
