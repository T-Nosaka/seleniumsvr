using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace seleniumsvr;

internal class Program
{
    /// <summary>
    /// エントリ
    /// </summary>
    static async Task Main(string[] args)
    {
        Logger.Log("Start");

        //コマンドライン引数を解析する
        var commandargs = CommandLine.Parser.Default.ParseArguments<CommandArgs>(args);
        BrowserSession.webdriverinfopath = commandargs.Value.WebdriverInfoPath;

        var builder = Host.CreateApplicationBuilder(args);

        // stdout は MCP JSON-RPC 専用のため、コンソールログを完全に無効化する
        // （.NET ホストのログが stdout に混入すると Claude Desktop が JSON エラーを出す）
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SeleniumsvrLoggerProvider());

        //ブラウザセッションはプロセス寿命で共有（シングルトン）
        builder.Services.AddSingleton<BrowserSession>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        using var host = builder.Build();

        //MCPサーバー停止時にChromeを確実に閉じる
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            Logger.Log("cntchrome stopping.");
            host.Services.GetRequiredService<BrowserSession>().Dispose();
        });

        await host.RunAsync();

        Logger.Log("Stop");
    }

    /// <summary>
    /// 実行引数管理
    /// </summary>
    public class CommandArgs
    {
        /// <summary>
        /// webdriverinfoパス
        /// --webdriverinfo
        /// </summary>
        [CommandLine.Option("webdriverinfo")]
        public string? WebdriverInfoPath { get; set; } = null;

    }
}
