# seleniumsvr — WebBrowser MCP Server

Selenium WebDriver をラップした **MCP (Model Context Protocol) サーバー** です。
Claude Desktop などの MCP クライアントから Chrome ブラウザを操作できるようにします。

## Features

| Category | Tools |
|---|---|
| **Navigation** | `navigate` `go_back` `go_forward` `reload` `close_browser` |
| **Page Info** | `get_title` `get_current_url` `get_page_text` `get_page_source` `screenshot` |
| **Element** | `find_element` `click` `input_text` `get_hrefs` `get_tree` `list_section` `wait_for_element` |
| **Download** | `set_download_dir` `get_download_dir` `list_downloads` `wait_for_download` |
| **Window/Tab** | `list_windows` `switch_window` `close_window` |
| **Script** | `execute_script` |

## Requirements

- .NET 10.0 SDK
- Windows (ChromeDriver 依存)
- Google Chrome
- ChromeDriver (Chrome バージョンに一致)

## Setup

### 1. WebDriver の準備

[Chrome for Testing](https://googlechromelabs.github.io/chrome-for-testing/) から
Chrome と ChromeDriver をダウンロードし、任意のフォルダに配置します。

### 2. 設定ファイル

`seleniumsvr/webdriverinfo.json` を作成・編集します。

```json
{
  "Chrome": "C:\\webdriver\\chrome-win64\\chrome.exe",
  "WebDriver": "C:\\webdriver\\chromedriver-win64\\chromedriver.exe",
  "Download": "C:\\webdriver\\download",
  "Args": [
    "--user-data-dir=C:\\webdriver\\profile",
    "--profile-directory=profile.cfg"
  ]
}
```

`--webdriverinfo` オプションで外部の JSON ファイルを指定することもできます。

### 3. ビルド

```bash
dotnet build seleniumsvr/seleniumsvr.csproj
```

### 4. MCP Bundle（mcpb）の作成

リリースビルドしたバイナリと `manifest.json` を以下の構成で配置し、`mcpb pack` でバンドルファイルを作成します。

```
seleniumsvr
       |- win-x64
       |       |- seleniumsvr.exe
       |- manifest.json
```

```bash
mcpb pack
```

### 5. OpenCode への登録（WSL 環境例）

`~/.config/opencode/opencode.jsonc`:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "seleniumsvr": {
      "type": "local",
      "command": [
        "/mnt/c/webdriver/seleniumsvr.exe",
        "--webdriverinfo",
        "C:\\webdriver\\webdriverinfo.json"
      ],
      "enabled": true
    }
  }
}
```

## Architecture

```
┌─────────────────┐     stdio JSON-RPC      ┌──────────────────────┐
│  MCP Client     │ ◄─────────────────────► │  seleniumsvr         │
│  (Claude etc.)  │                          │  (MCP Server)        │
└─────────────────┘                          │                      │
                                             │  ┌────────────────┐  │
                                             │  │ BrowserSession │  │
                                             │  │  (Singleton)   │──┼──► ChromeDriver ──► Chrome
                                             │  └────────────────┘  │
                                             │  ┌────────────────┐  │
                                             │  │ MCP Tools      │  │
                                             │  │ (Auto-discover)│  │
                                             │  └────────────────┘  │
                                             └──────────────────────┘
```

- **BrowserSession**: ChromeDriver のライフサイクル管理（遅延初期化・排他制御・自動復旧・自動アタッチ）
- **MCP Tools**: 属性 `[McpServerTool]` で自動検出され、各操作を公開
- **Logger**: stdout は MCP JSON-RPC 専用。ログはファイル出力

## Key Behaviors

- **Lazy Start**: 初回 `navigate` で Chrome を起動
- **Auto Recovery**: Chrome が手動で閉じられても自動再起動
- **Auto Attach**: クリックなどで新規 window/tab が開くと自動的にアタッチ
- **Thread Safety**: 全操作 `lock` で直列化

## License

MIT
