using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace SFSearch.Mcp;

/// <summary>
/// The MCP front door to the SFSearch service (2026-11-07). One small adapter per Claude session. It never reads a
/// plugin: it starts the service if needed, passes each tool call to it (several at once), and returns the answer.
/// stdout carries MCP messages only; anything else goes to stderr, so a stray line can't corrupt the channel.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Protect the protocol channel: anything written with Console.Write goes to stderr.
        var realOut = Console.Out;
        Console.SetOut(Console.Error);
        // Pipe connects block a thread each while they wait: start with enough threads for many calls at once
        ThreadPool.SetMinThreads(128, 128);
        Bridge.Home = FindHome(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("[sfsearch-mcp] crash: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Console.Error.WriteLine("[sfsearch-mcp] " + e.Exception.GetBaseException().Message); e.SetObserved(); };

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "sfsearch", Version = "1.0" };
                o.ServerInstructions = Tools.Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<Tools>();
        try
        {
            await builder.Build().RunAsync();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[sfsearch-mcp] stopped: " + e);
            return 1;
        }
        finally { GC.KeepAlive(realOut); }
    }

    /// <summary>The SFSearch folder: --home, SFSEARCH_HOME, or the SFSearch folder beside this adapter's project.</summary>
    static string FindHome(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--home", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && Directory.Exists(args[i + 1])) return Path.GetFullPath(args[i + 1]);
        string? env = Environment.GetEnvironmentVariable("SFSEARCH_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "SFSearch");
            if (File.Exists(Path.Combine(candidate, "SFSearch.csproj"))) return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "SFSearch.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        // Not found: tools then answer that sfs.ps1 wasn't found here, and to pass --home
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
