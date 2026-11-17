using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SFSearch.Service;

namespace SFSearch
{
    /// <summary>
    /// The command-line side of the SFSearch service (Service\ServiceHost.cs). `Service start` (or `Session start`) opens it;
    /// while it runs, every sfs.cmd command goes to it. Agents normally reach it through the MCP adapter (SFSearch.Mcp),
    /// which starts it by itself. It has no idle shutdown: `Service stop` closes it.
    /// </summary>
    public static class SessionServer
    {
        /// <summary>True inside a service worker.</summary>
        public static bool Active;

        public static bool IsRunning() => !Active && Wire.RunningService(Home.Dir) != null;

        public static void Command(List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "start": Start(); break;
                case "restart":
                    if (Wire.RunningService(Home.Dir) != null) Stop(quiet: true);
                    Start();
                    break;
                case "stop": Stop(quiet: false); break;
                case "status":
                    if (Wire.RunningService(Home.Dir) == null) { Console.WriteLine("The service isn't running. Commands still run: each reads what it needs from the plugin files."); if (Home.OldDataLine(Home.Dir) is { } oldData) Console.WriteLine(oldData); return; }
                    Console.Write(Call(new Request { Op = "status" }).Out);
                    break;
                case "config":
                    Console.WriteLine(ServiceConfig.Describe(Home.Dir, Home.SettingsOutsideFile()));
                    break;
                default:
                    Console.WriteLine("Usage: SFSearch Service start | stop | restart | status | config   (Session is the same command. For agents: while the service runs, every command goes to it. Settings: sfsearch.json; 'Service config' shows every setting in effect and where it came from)");
                    break;
            }
        }

        static void Stop(bool quiet)
        {
            var p = Wire.RunningService(Home.Dir);
            if (p == null) { if (!quiet) Console.WriteLine("The service isn't running."); return; }
            var r = Call(new Request { Op = "stop" });
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (Exception) { } } // it lets running commands finish first (up to 15 s)
            if (!quiet) Console.Write(r.Error ?? r.Out);
        }

        public static void Start()
        {
            // Several agents may start it at the same moment: one at a time; the others find it running.
            using var gate = new Mutex(false, @"Local\sfsearch_start_" + Wire.Hash(Home.Dir));
            try { gate.WaitOne(TimeSpan.FromSeconds(90)); } catch (AbandonedMutexException) { }
            try { StartLocked(); }
            finally { try { gate.ReleaseMutex(); } catch (ApplicationException) { } }
        }

        static void StartLocked()
        {
            if (Wire.RunningService(Home.Dir) is { } p) { Console.WriteLine($"The service is already running (process {p.Id}). Every command goes to it until 'Service stop'."); return; }
            // Run from a copy of the program, so sfs.cmd can still rebuild SFSearch while the service runs.
            string bin = AppContext.BaseDirectory;
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "SFSearch.exe");
            string root = Path.Combine(Path.GetTempPath(), "sfsearch_service_bin");
            string copy = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Environment.ProcessId);
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.GetFiles(bin, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(bin, f);
                if (rel.StartsWith("Cache", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, rel))!);
                File.Copy(f, Path.Combine(copy, rel), true);
            }
            // Old copies go, but never one a running service (or its workers) uses: deleting part of it would leave its
            // workers without their files. A copy is in use while its exe can't be opened exclusively.
            foreach (var old in Directory.GetDirectories(root).Where(d => d != copy))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(old) < TimeSpan.FromMinutes(10)) continue; // another starter's, just made
                    string oldExe = Path.Combine(old, exeName);
                    if (File.Exists(oldExe)) using (new FileStream(oldExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    Directory.Delete(old, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string exe = Path.Combine(copy, exeName);
            // A hidden window of its own, so it never holds the caller's console open. The service itself runs .NET's
            // workstation garbage collector (J59): the program starts on server for a command run without the service
            // (SFSearch.csproj), and the service only hands out work. A shell start takes this process's environment.
            string? gcWas = Environment.GetEnvironmentVariable("DOTNET_gcServer"), datasWas = Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode");
            Environment.SetEnvironmentVariable("DOTNET_gcServer", "0");
            Environment.SetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode", null);
            try { Process.Start(new ProcessStartInfo(exe, $"service run \"{Home.Dir}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_gcServer", gcWas);
                Environment.SetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode", datasWas);
            }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 30)
            {
                if (Wire.RunningService(Home.Dir) is { } started)
                {
                    try
                    {
                        Wire.CallAsync(Home.Dir, new Request { Op = "ping" }, 2000, CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine($"Service started (process {started.Id}). Every SFSearch command now goes to it until 'Service stop'. Plugins are read live; working files are in the scratch folder, which was emptied now.");
                        return;
                    }
                    catch (Exception e) when (e is IOException or TimeoutException) { }
                }
                Thread.Sleep(200);
            }
            Console.WriteLine("Error: the service didn't start within 30 s. Its log is in Service\\logs.");
            Environment.ExitCode = 1;
        }

        static Reply Call(Request req)
        {
            req.Cwd ??= Home.CallerCwd;
            req.Agent ??= Environment.GetEnvironmentVariable("SFSEARCH_AGENT") ?? "cli";
            try { return Wire.CallAsync(Home.Dir, req, 30000, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                return new Reply { Done = true, Code = 2, Error = $"Error: the service couldn't be reached ({e.Message}). 'SFSearch Service status' tells whether it runs; 'Service stop' then 'Service start' restarts it." };
            }
        }

        /// <summary>Runs a command in the service and prints its answer here.</summary>
        public static int Client(string[] args)
        {
            var list = args.ToList();
            // --stdin: the service can't read this console, so the text goes in a file
            int si = list.FindIndex(a => a.Equals("--stdin", StringComparison.OrdinalIgnoreCase));
            string? tmp = null;
            if (si >= 0)
            {
                string dir = Path.Combine(Home.Dir, "Service", "tmp");
                Directory.CreateDirectory(dir);
                tmp = Path.Combine(dir, $"stdin_{Environment.ProcessId}_{DateTime.Now.Ticks}.sql");
                File.WriteAllText(tmp, Console.In.ReadToEnd());
                list[si] = "--file";
                list.Insert(si + 1, tmp);
            }
            // Ctrl+C here stops the command in the service too (by the call's key: the job number isn't known yet)
            string key = Wire.NewKey();
            ConsoleCancelEventHandler onBreak = (_, _) =>
            {
                try { Wire.CallAsync(Home.Dir, new Request { Op = "cancel", Key = key }, 2000, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { }
            };
            Console.CancelKeyPress += onBreak;
            try
            {
                var r = Call(new Request { Op = "run", Args = list.ToArray(), WaitMs = -1, Key = key });
                // Commands only read: one cut off by a service restart is sent again once, when the service is back
                if (r.Err.Contains("the service stopped before this command finished") || (r.Error != null && r.Error.Contains("couldn't be reached")))
                {
                    for (int i = 0; i < 60 && Wire.RunningService(Home.Dir) == null; i++) Thread.Sleep(500);
                    if (Wire.RunningService(Home.Dir) != null) r = Call(new Request { Op = "run", Args = list.ToArray(), WaitMs = -1, Key = key });
                }
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
                if (r.Error != null) { Console.Error.WriteLine(r.Error); return r.Code == 0 ? 2 : r.Code; }
                Console.Out.Write(r.Out);
                Console.Error.Write(r.Err);
                return r.Code;
            }
            finally
            {
                Console.CancelKeyPress -= onBreak;
                if (tmp != null) try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }
}
