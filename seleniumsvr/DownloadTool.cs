using ModelContextProtocol.Server;
using System.ComponentModel;

namespace seleniumsvr;

/// <summary>
/// ダウンロードファイル制御系 MCP ツール。
/// Chrome 起動前はフォルダを記憶し、起動時に ChromeOptions で適用する。
/// 起動後の変更は CDP（Browser.setDownloadBehavior）でリアルタイム反映する。
/// </summary>
[McpServerToolType]
public sealed class DownloadTool
{
    /// <summary>ブラウザセッション本体（DI注入）</summary>
    private readonly BrowserSession _session;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="session">ブラウザセッション</param>
    public DownloadTool(BrowserSession session)
    {
        _session = session;
    }

    /// <summary>
    /// ダウンロードフォルダを変更する
    /// </summary>
    /// <param name="path">絶対パスのフォルダ（存在しない場合は自動作成）</param>
    /// <returns>設定されたフォルダパス、またはエラー文字列</returns>
    [McpServerTool(Name = "set_download_dir"),
     Description("Set where Chrome will save downloaded files. Call this before clicking download buttons or links. Works even if Chrome is already running. Create a specific folder for your downloads, then set_download_dir, find and click the download button, then wait_for_download to confirm the file arrived.")]
    public string SetDownloadDir(
        [Description("Absolute path of the folder to save downloads (e.g. C:\\Users\\me\\Downloads).")]
        string path)
    {
        try
        {
            _session.SetDownloadDir(path);
            return $"download directory set: {_session.GetDownloadDir()}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 現在のダウンロードフォルダを取得する
    /// </summary>
    /// <returns>フォルダパス。未設定の場合はその旨のメッセージ</returns>
    [McpServerTool(Name = "get_download_dir"),
     Description("Get the current download directory. Use before downloading to verify where files will be saved, or before list_downloads to know which folder to check. Returns the configured directory path.")]
    public string GetDownloadDir()
    {
        try
        {
            var dir = _session.GetDownloadDir();
            return string.IsNullOrEmpty(dir)
                ? "download directory is not configured."
                : dir;
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// ダウンロードフォルダ内のファイル一覧を返す（ダウンロード中を除外）
    /// </summary>
    /// <returns>ファイル名・サイズ・更新日時の一覧、またはエラー文字列</returns>
    [McpServerTool(Name = "list_downloads"),
     Description("List all completed files in the download directory. Shows file names, sizes, and dates. Use after downloading to verify files arrived, or instead of wait_for_download if you just need to check what's been downloaded. Automatically excludes in-progress downloads (.crdownload files).")]
    public string ListDownloads()
    {
        try
        {
            var files = _session.ListDownloads();
            if (files.Count == 0)
                return "no files found in download directory.";

            return string.Join("\n", files);
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// 指定パターンのファイルがダウンロード完了するまで待機する
    /// </summary>
    /// <param name="pattern">ファイル名パターン（例: "*.pdf", "report_*.xlsx"）</param>
    /// <param name="timeoutSeconds">タイムアウト秒数（既定: 60）</param>
    /// <returns>完了ファイルのフルパス、タイムアウト時はその旨のメッセージ</returns>
    [McpServerTool(Name = "wait_for_download"),
     Description("Wait for a file to finish downloading. Workflow: set_download_dir → find and click download button → wait_for_download to confirm. Use patterns like '*.pdf', 'report_*.xlsx', or '*' to match any file. Detects completion when .crdownload temporary files disappear and size stabilizes.")]
    public string WaitForDownload(
        [Description("File name pattern to match (e.g. '*.pdf', 'report_*.xlsx', '*').")]
        string pattern,
        [Description("Timeout in seconds. Default: 60.")]
        int timeoutSeconds = 60)
    {
        try
        {
            var result = _session.WaitForDownload(pattern, timeoutSeconds);
            return result is null
                ? $"timed out waiting for '{pattern}' after {timeoutSeconds}s."
                : $"downloaded: {result}";
        }
        catch (Exception ex) { return $"ERROR: {ex.GetType().Name}: {ex.Message}"; }
    }
}
