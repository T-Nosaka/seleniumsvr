# seleniumsvr — WebBrowser MCP Server

Selenium WebDriver をラップした **MCP (Model Context Protocol) サーバー** です。
Claude Desktop などの MCP クライアントから Chrome / Firefox ブラウザを操作できるようにします。

## Features

| Category | Tools |
|---|---|
| **Lifecycle** | `prepare_browser` `release_browser` `session_status` `list_browser` `close_browser` |
| **Navigation** | `navigate` `go_back` `go_forward` `reload` |
| **Page Info** | `get_title` `get_current_url` `get_page_text` `get_page_source` `screenshot` |
| **Element** | `find_element` `click` `input_text` `get_hrefs` `get_tree` `list_section` `wait_for_element` |
| **Download** | `set_download_dir` `get_download_dir` `list_downloads` `wait_for_download` |
| **Window/Tab** | `list_windows` `switch_window` `close_window` |
| **Script** | `execute_script` |

## Requirements

- .NET 10.0 SDK
- Windows
- **Chrome 使用時**: Google Chrome + ChromeDriver (Chrome バージョンに一致)
- **Firefox 使用時**: Mozilla Firefox + [GeckoDriver](https://github.com/mozilla/geckodriver/releases)

## Setup

### 1. WebDriver の準備

**Chrome の場合:**
[Chrome for Testing](https://googlechromelabs.github.io/chrome-for-testing/) から
Chrome と ChromeDriver をダウンロードし、任意のフォルダに配置します。

**Firefox の場合:**
[Mozilla Firefox](https://www.mozilla.org/firefox/) と
[GeckoDriver](https://github.com/mozilla/geckodriver/releases) をダウンロードし、任意のフォルダに配置します。

### 2. 設定ファイル

`seleniumsvr/webdriverinfo.json` を作成・編集します。
**名前付きで複数のブラウザ定義**を持てる形式です。`Browsers` の下に任意の名前で定義を並べ、
`prepare_browser` の `profile` 引数でどれを使うかを選びます（省略時は `default`）。

```json
{
  "Browsers": {
    "default": {
      "BrowserType": "chrome",
      "Browser": "C:\\webdriver\\chrome-win64\\chrome.exe",
      "WebDriver": "C:\\webdriver\\chromedriver-win64\\chromedriver.exe",
      "Download": "C:\\webdriver\\download",
      "Args": ["--user-data-dir=C:\\webdriver\\profile\\default"]
    },
    "shopping": {
      "BrowserType": "chrome",
      "Browser": "C:\\webdriver\\chrome-win64\\chrome.exe",
      "WebDriver": "C:\\webdriver\\chromedriver-win64\\chromedriver.exe",
      "Download": "C:\\webdriver\\download",
      "Args": ["--user-data-dir=C:\\webdriver\\profile\\shopping"]
    },
    "work-firefox": {
      "BrowserType": "firefox",
      "Browser": "C:\\Program Files\\Mozilla Firefox\\firefox.exe",
      "WebDriver": "C:\\webdriver\\geckodriver\\geckodriver.exe",
      "Download": "C:\\webdriver\\download",
      "Args": ["-profile", "C:\\webdriver\\profile\\work-firefox"]
    }
  }
}
```

各定義のフィールド:

| Field | 説明 |
|---|---|
| `BrowserType` | `"chrome"` または `"firefox"` |
| `Browser` | ブラウザ実行体のパス |
| `WebDriver` | ChromeDriver / GeckoDriver のパス |
| `Download` | ダウンロード先フォルダ |
| `Args` | 起動引数。Chrome は `--user-data-dir=<path>`、Firefox は `-profile <path>` でプロファイルを指定 |

利用可能な定義名は `list_browser` で確認できます。

> **後方互換**: `Browsers` を持たない従来のフラット形式（単一定義をトップレベルに書く形）もそのまま動作し、その定義は `default` として読み込まれます。

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
                                             │  │  (Singleton)   │──┼──► WebDriver ──► Chrome / Firefox
                                             │  └────────────────┘  │
                                             │  ┌────────────────┐  │
                                             │  │ MCP Tools      │  │
                                             │  │ (Auto-discover)│  │
                                             │  └────────────────┘  │
                                             └──────────────────────┘
```

- **BrowserSession**: WebDriver のライフサイクル管理（遅延初期化・排他制御・自動復旧・自動アタッチ・プロファイル排他ロック）
- **MCP Tools**: 属性 `[McpServerTool]` で自動検出され、各操作を公開
- **Logger**: stdout は MCP JSON-RPC 専用。ログはファイル出力

## Key Behaviors

- **Declarative Lifecycle**: `prepare_browser` でブラウザ定義（プロファイル）を選択して起動し、`release_browser` で解放
- **Profile Lock**: 永続プロファイルは 1 セッション占有。別セッションからの同時使用は明確なエラーで拒否
- **Lazy Start**: 未 `prepare` のまま初回 `navigate` すると `default` 定義でブラウザを起動
- **Auto Recovery**: ブラウザが手動で閉じられても自動再起動
- **Auto Attach**: クリックなどで新規 window/tab が開くと自動的にアタッチ
- **Thread Safety**: 全操作 `lock` で直列化

## Profiles & Concurrency（プロファイルと同時実行）

ブラウザのプロファイル（Cookie・ログイン状態など）は **1 プロファイルにつき同時に 1 セッションのみ** 操作できます。
これはブラウザ側の制約（1 つのプロファイルは 1 プロセスしか開けない）に沿った設計で、本サーバーは次のように扱います。

- **宣言的な準備 / 解放**: `prepare_browser(profile)` で使用するブラウザ定義を宣言し、`release_browser` で解放します。
  未 `prepare` のまま `navigate` した場合は `default` 定義で自動起動します（後方互換）。
- **プロファイル排他ロック**: `prepare_browser`（および遅延起動）時に、永続プロファイルのフォルダへ排他ロックを取得します。
  別のセッション（別プロセス）が同じプロファイルを掴もうとすると、`別のセッションが使用中です` という
  明確なエラーで即座に失敗します（ブラウザ内部の不可解なロック衝突まで到達しません）。
- **1 セッション 1 ブラウザ**: 1 つのセッション内では常に 1 つのブラウザだけが稼働します。
  稼働中に別のプロファイルを `prepare_browser` すると `先に release_browser を呼んでください` と拒否されます。
  切り替えるには一度 `release_browser` してください。
- **ロックの解放**: `release_browser` は best-effort の早期解放です。呼び忘れても、サーバープロセス終了時に
  必ずロックは解放されます。

### 複数プロファイルの同時実行

stdio 接続では **1 セッション = 1 プロセス** です。異なるプロファイルを同時に動かしたい場合は、
MCP クライアントで**複数のセッション（プロセス）**を起動してください。各セッションが別々のプロファイルを
`prepare_browser` する限り、ロックは衝突せず並行して動作します。
同じプロファイルを 2 つのセッションから掴もうとしたときだけ、2 つ目がロックエラーで弾かれます。

## License

MIT
