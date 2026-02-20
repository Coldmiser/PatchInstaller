using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;
using System;
using System.Reflection;

namespace PatchInstaller
{
    internal static class Program
    {
        // Exit codes to help automation
        // 0 = No updates found
        // 1 = Updates installed (or attempted) and no reboot required
        // 2 = Updates installed (or attempted) and reboot required
        // 3 = Fatal error
        private const int ExitNoUpdates = 0;
        private const int ExitInstalledNoReboot = 1;
        private const int ExitInstalledNeedsReboot = 2;
        private const int ExitFatal = 3;

        public static int Main(string[] args)
        {
            ConfigureLogging();

            try
            {
                LogStartupInfo(args);

                if (!IsRunningAsAdmin())
                {
                    Log.Error("This application must be run as Administrator. Exiting.");
                    return ExitFatal;
                }

                // Optional: If a reboot is already pending, log it (doesn't stop installs, but very useful)
                var pendingBefore = IsRebootPendingFromRegistry(out var pendingSignalsBefore);

                // PendingBefore = Yes or No, Signals = [list of signals, e.g. WU:RebootRequired, CBS:RebootPending, etc.] (empty if no signals)
                var PendingBeforeText = pendingBefore ? "Yes" : "No";

                Log.Information("Pre-check pending reboot: {Pending}.",
                    PendingBeforeText);
    //            Log.Information("Pre-check Signals: {Signals}",
    //                string.Join(", ", pendingSignalsBefore));

                var criteria = ParseCriteria(args) ?? "IsInstalled=0 and IsHidden=0 and Type='Software'";
    //            Log.Information("Windows Update search criteria: {Criteria}", criteria);

                // --- WUA search ---
                var searchResult = WindowsUpdateClient.SearchUpdates(criteria);
                LogSearchSummary(searchResult);

                if (searchResult.Updates.Count == 0)
                {
                    Log.Information("No applicable updates found. System appears up to date.");
                    return ExitNoUpdates;
                }

                // --- Log update metadata (before EULA acceptance) ---
                foreach (var update in searchResult.Updates)
                    WindowsUpdateClient.LogUpdateDetails(update);

                // --- Accept EULAs (required for some updates before download/install) ---
                WindowsUpdateClient.AcceptEulas(searchResult.Updates);

                // --- Download ---
                var downloadResult = WindowsUpdateClient.DownloadUpdates(searchResult.Updates);
                LogDownloadSummary(downloadResult);

                // Filter: only downloaded updates proceed to install
                var downloadable = WindowsUpdateClient.FilterDownloadedUpdates(searchResult.Updates);
                Log.Information("Downloaded updates available for installation: {Count}", downloadable.Count);

                if (downloadable.Count == 0)
                {
                    Log.Warning("No updates were downloaded successfully; nothing to install.");
                    // Reboot check still useful (some environments set reboot pending independently)
                    var pendingNow = IsRebootPendingFromRegistry(out var signalsNow);
                    Log.Information("Post-run pending reboot (registry): {Pending}. Signals: {Signals}",
                        pendingNow, string.Join(", ", signalsNow));
                    return pendingNow ? ExitInstalledNeedsReboot : ExitInstalledNoReboot;
                }

                // --- Install ---
                var installResult = WindowsUpdateClient.InstallUpdates(downloadable);
                LogInstallSummary(installResult);

                // --- Reboot required? ---
                // WUA installation result contains reboot requirement flag. [2](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)
                var wuaSaysReboot = installResult.RebootRequired;

                // Registry-based signals catch cases like CBS pending, pending file rename ops, etc. [6](https://adamtheautomator.com/pending-reboot-registry/)[5](https://learn.microsoft.com/en-us/answers/questions/3876101/what-causes-registry-that-indicates-reboot-pending)
                var registrySaysReboot = IsRebootPendingFromRegistry(out var pendingSignalsAfter);

                Log.Information("Reboot check: WUA says reboot required = {WuaReboot}, Registry says pending reboot = {RegReboot}, Signals = {Signals}",
                    wuaSaysReboot, registrySaysReboot, string.Join(", ", pendingSignalsAfter));

                var rebootRequired = wuaSaysReboot || registrySaysReboot;

                if (rebootRequired)
                {
                    Log.Warning("A reboot is required to complete update installation.");
                    return ExitInstalledNeedsReboot;
                }

                Log.Information("No reboot required.");
                return ExitInstalledNoReboot;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error occurred.");
                return ExitFatal;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        // ------------------------
        // Logging + Diagnostics
        // ------------------------

        private static void ConfigureLogging()
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "patch-installer-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.WithMachineName()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.FromLogContext()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            Log.Information("--------------------------", logPath);
            Log.Information("Log file configured: {LogPath}", logPath);
            Log.Information("");
            LogAssemblyInfo();
            // Writing to both console and file uses multiple "sinks". [3](https://bing.com/search?q=Serilog+C%23+console+app+file+and+console+sinks+configuration)[4](https://github.com/serilog/serilog-sinks-console)
        }
        private static void LogAssemblyInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetName();
            var version = name.Version?.ToString() ?? "n/a";
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "n/a";

            Log.Information("Version: {Version}",
                version);
//            CheckVersion();
        }
        private static void LogStartupInfo(string[] args)
        {
            Log.Information("PatchInstaller starting. Arguments: {Args}", string.Join(" ", args));
            Log.Information("Process: {Proc} ({Pid}), User: {User}",
                Process.GetCurrentProcess().ProcessName,
                Process.GetCurrentProcess().Id,
                Environment.UserName);
            Log.Information("OS: {OS}",
                Environment.OSVersion);
            Log.Information("64-bit OS: {Is64BitOS}",
                Environment.Is64BitOperatingSystem);
            Log.Information("64-bit process: {Is64BitProc}",
                Environment.Is64BitProcess);
            Log.Information("");
            #if DEBUG
                Log.Debug("This Process is running in DEBUG mode and the debug-only code is enabled.");
                Log.Information("");
            #else
            //    Log.Debug("This Process is running in RELEASE mode.");
            #endif
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string? ParseCriteria(string[] args)
        {
            // Example: --criteria "IsInstalled=0 and IsHidden=0 and Type='Software'"
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--criteria", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static void LogSearchSummary(WindowsUpdateClient.UpdateSearchResult result)
        {
            Log.Information("Search completed. ResultCode={Code}, UpdatesFound={Count}",
                result.ResultCode, result.Updates.Count);
        }

        private static void LogDownloadSummary(WindowsUpdateClient.OperationResult result)
        {
            Log.Information("Download completed. ResultCode={Code}",
                result.ResultCode);
        }

        private static void LogInstallSummary(WindowsUpdateClient.InstallationOutcome outcome)
        {
            Log.Information("Install completed. ResultCode={Code}, RebootRequired={Reboot}",
                outcome.ResultCode, outcome.RebootRequired);

            foreach (var perUpdate in outcome.PerUpdateResults)
            {
                Log.Information("Update result: Title={Title}, ResultCode={Code}, RebootRequired={Reboot}",
                    perUpdate.Title, perUpdate.ResultCode, perUpdate.RebootRequired);
            }
        }

        // ------------------------
        // Reboot detection (registry)
        // ------------------------
        // Common sources set reboot-pending indicators in registry. [6](https://adamtheautomator.com/pending-reboot-registry/)[5](https://learn.microsoft.com/en-us/answers/questions/3876101/what-causes-registry-that-indicates-reboot-pending)

        private static bool IsRebootPendingFromRegistry(out List<string> signals)
        {
            signals = new List<string>();

            // 1) Windows Update reboot required key
            if (RegistryKeyExists(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                signals.Add("WU:RebootRequired");

            // 2) Component Based Servicing reboot pending
            if (RegistryKeyExists(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                signals.Add("CBS:RebootPending");

            // 3) Pending file rename operations
            if (RegistryValueExists(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
                signals.Add("SessionMgr:PendingFileRenameOperations");

            // 4) UpdateExeVolatile (non-zero often indicates reboot pending)
            if (RegistryValueExists(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Updates", "UpdateExeVolatile"))
                signals.Add("Updates:UpdateExeVolatile");

            return signals.Count > 0;
        }

        private static bool RegistryKeyExists(string fullPath)
        {
            // Registry.GetValue can't check key existence directly, so we open base keys.
            // We'll parse common HKLM only for simplicity.
            try
            {
                const string hklmPrefix = @"HKEY_LOCAL_MACHINE\";
                if (!fullPath.StartsWith(hklmPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                var subKeyPath = fullPath.Substring(hklmPrefix.Length);
                using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
                return key != null;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed checking registry key existence: {Path}", fullPath);
                return false;
            }
        }

        private static bool RegistryValueExists(string keyPath, string valueName)
        {
            try
            {
                // keyPath must be an HKLM path for this helper.
                const string hklmPrefix = @"HKEY_LOCAL_MACHINE\";
                if (!keyPath.StartsWith(hklmPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                var subKeyPath = keyPath.Substring(hklmPrefix.Length);
                using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
                if (key == null) return false;

                return key.GetValue(valueName) != null;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed checking registry value existence: {KeyPath} {ValueName}", keyPath, valueName);
                return false;
            }
        }
    }

    internal static class WindowsUpdateClient
    {
        // Minimal wrapper objects so Program.cs isn't polluted with COM/dynamic types.
        // WUA object model includes UpdateSession, UpdateSearcher, UpdateDownloader, UpdateInstaller, etc. [2](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)

        internal sealed class UpdateSearchResult
        {
            public required string ResultCode { get; init; }
            public required List<dynamic> Updates { get; init; }
        }

        internal sealed class OperationResult
        {
            public required string ResultCode { get; init; }
            public required int HResult { get; init; }
        }

        internal sealed class InstallationOutcome
        {
            public required string ResultCode { get; init; }
            public required int HResult { get; init; }
            public required bool RebootRequired { get; init; }
            public required List<PerUpdateInstallationResult> PerUpdateResults { get; init; }
        }

        internal sealed class PerUpdateInstallationResult
        {
            public required string Title { get; init; }
            public required string ResultCode { get; init; }
            public required int HResult { get; init; }
            public required bool RebootRequired { get; init; }
        }

        // ------------------------
        // Core WUA operations
        // ------------------------

        public static UpdateSearchResult SearchUpdates(string criteria)
        {
            // Based on Microsoft guidance for searching/downloading/installing updates using WUA. [1](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/searching--downloading--and-installing-updates)[2](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)
            dynamic session = CreateUpdateSession();
            session.ClientApplicationID = "PatchInstaller (Serilog)";

            dynamic searcher = session.CreateUpdateSearcher();
            Log.Information("Searching for updates...");
            dynamic result = searcher.Search(criteria);

            var updates = new List<dynamic>();
            dynamic updatesCom = result.Updates;
            for (int i = 0; i < updatesCom.Count; i++)
                updates.Add(updatesCom.Item(i));

            return new UpdateSearchResult
            {
                ResultCode = SafeToString(result.ResultCode),
                Updates = updates
            };
        }

        public static void AcceptEulas(List<dynamic> updates)
        {
            foreach (var update in updates)
            {
                try
                {
                    bool eulaAccepted = SafeToBool(update.EulaAccepted);
                    if (!eulaAccepted)
                    {
                        Log.Information("Accepting EULA for: {Title}", SafeToString(update.Title));
                        update.AcceptEula();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to accept EULA for update: {Title}", SafeToString(update.Title));
                }
            }
        }

        public static OperationResult DownloadUpdates(List<dynamic> updates)
        {
            dynamic session = CreateUpdateSession();
            session.ClientApplicationID = "PatchInstaller (Serilog)";

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = ToUpdateCollection(updates);

            Log.Information("Downloading {Count} updates...", updates.Count);
            dynamic result = downloader.Download();

            return new OperationResult
            {
                ResultCode = SafeToString(result.ResultCode),
                HResult = SafeToInt(result.HResult)
            };
        }

        public static List<dynamic> FilterDownloadedUpdates(List<dynamic> updates)
        {
            var downloaded = new List<dynamic>();
            foreach (var update in updates)
            {
                try
                {
                    // IsDownloaded indicates payload present
                    if (SafeToBool(update.IsDownloaded))
                        downloaded.Add(update);
                    else
                        Log.Warning("Not downloaded (skipping install): {Title}", SafeToString(update.Title));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed checking IsDownloaded for: {Title}", SafeToString(update.Title));
                }
            }
            return downloaded;
        }

        public static InstallationOutcome InstallUpdates(List<dynamic> updates)
        {
            dynamic session = CreateUpdateSession();
            session.ClientApplicationID = "PatchInstaller (Serilog)";

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = ToUpdateCollection(updates);

            Log.Information("Installing {Count} updates...", updates.Count);
            dynamic result = installer.Install();

            // Per-update results are available via result.GetUpdateResult(i) in WUA. [2](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)
            var perUpdateResults = new List<PerUpdateInstallationResult>();
            for (int i = 0; i < updates.Count; i++)
            {
                try
                {
                    dynamic updateResult = result.GetUpdateResult(i);
                    perUpdateResults.Add(new PerUpdateInstallationResult
                    {
                        Title = SafeToString(updates[i].Title),
                        ResultCode = SafeToString(updateResult.ResultCode),
                        HResult = SafeToInt(updateResult.HResult),
                        RebootRequired = SafeToBool(updateResult.RebootRequired)
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed reading per-update install result for: {Title}", SafeToString(updates[i].Title));
                }
            }

            return new InstallationOutcome
            {
                ResultCode = SafeToString(result.ResultCode),
                HResult = SafeToInt(result.HResult),
                RebootRequired = SafeToBool(result.RebootRequired),
                PerUpdateResults = perUpdateResults
            };
        }

        // ------------------------
        // Update metadata logging
        // ------------------------

        public static void LogUpdateDetails(dynamic update)
        {
            try
            {
                var title = SafeToString(update.Title);
                var description = SafeToString(update.Description);
                var isDownloaded = SafeToBool(update.IsDownloaded);
                var isInstalled = SafeToBool(update.IsInstalled);
                var rebootBehavior = SafeToString(update.InstallationBehavior?.RebootBehavior);
                var rebootBehaviorText = rebootBehavior ? "Yes" : "No";

                string identity = "n/a";
                try
                {
                    identity = $"{SafeToString(update.Identity?.UpdateID)} rev {SafeToString(update.Identity?.RevisionNumber)}";
                }
                catch { /* ignore */ }

                string kbList = "n/a";
                try
                {
                    // KBArticleIDs is a collection
                    kbList = JoinComCollection(update.KBArticleIDs);
                }
                catch { /* ignore */ }

                string categories = "n/a";
                try
                {
                    categories = JoinCategories(update.Categories);
                }
                catch { /* ignore */ }

                Log.Information("---------------------------");
                Log.Information("Update found: {Title}", title);
                //Log.Debug("  Identity: {Identity}", identity);
                Log.Debug("  Installed={Installed}, Downloaded={Downloaded}, RebootBehavior={RebootBehavior}",
                    isInstalled, isDownloaded, rebootBehaviorText);
                Log.Debug("  KBs: {KBs}", kbList);
                Log.Debug("  Categories: {Categories}", categories);
                Log.Debug("  Description: {Description}", Truncate(description, 600));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed logging update details.");
            }
        }

        // ------------------------
        // COM helper methods
        // ------------------------

        private static dynamic CreateUpdateSession()
        {
            // ProgID for WUA session: Microsoft.Update.Session [2](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)
            var t = Type.GetTypeFromProgID("Microsoft.Update.Session")
                    ?? throw new InvalidOperationException("WUA COM object 'Microsoft.Update.Session' not available.");
            return Activator.CreateInstance(t)!;
        }

        private static dynamic ToUpdateCollection(List<dynamic> updates)
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")
                    ?? throw new InvalidOperationException("WUA COM object 'Microsoft.Update.UpdateColl' not available.");
            dynamic coll = Activator.CreateInstance(t)!;

            foreach (var u in updates)
                coll.Add(u);

            return coll;
        }

        private static string JoinComCollection(dynamic comCollection)
        {
            try
            {
                int count = (int)comCollection.Count;
                var items = new List<string>();
                for (int i = 0; i < count; i++)
                    items.Add(SafeToString(comCollection.Item(i)));
                return items.Count == 0 ? "n/a" : string.Join(", ", items);
            }
            catch
            {
                return "n/a";
            }
        }

        private static string JoinCategories(dynamic categories)
        {
            try
            {
                int count = (int)categories.Count;
                var items = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    dynamic cat = categories.Item(i);
                    items.Add(SafeToString(cat.Name));
                }
                return items.Count == 0 ? "n/a" : string.Join(", ", items);
            }
            catch
            {
                return "n/a";
            }
        }

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "n/a";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string SafeToString(object? o) => o?.ToString() ?? "n/a";

        private static bool SafeToBool(object? o)
        {
            if (o == null) return false;
            if (o is bool b) return b;
            bool.TryParse(o.ToString(), out var result);
            return result;
        }

        private static int SafeToInt(object? o)
        {
            if (o == null) return 0;
            if (o is int i) return i;
            int.TryParse(o.ToString(), out var result);
            return result;
        }
    }
}
