// Starts the replacement script that finishes the update after this process exits.
//
// A running program cannot overwrite its own exe, so the last step is always a separate
// process: scripts\invoke-update.ps1 waits for our PID to disappear, backs up Performix.exe,
// unpacks the zip and restarts the new build (see docs\RELEASE.md). This type only resolves
// the script and starts it; every decision about the swap belongs to the script so a crash
// here cannot half-install anything.
//
// The script ships next to the exe (the csproj copies scripts\invoke-update.ps1 to
// <output>\scripts\), and a repository layout is accepted as a second chance so a build run
// straight out of bin\ still works.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Outcome of trying to launch the replacement script.</summary>
    public sealed record UpdateInstallStart(bool Ok, string? Reason, string? Detail, string? ScriptPath, string? LogPath);

    internal static class UpdateInstaller
    {
        private const string ScriptName = "invoke-update.ps1";

        /// <summary>Where the script and the app both append their progress.</summary>
        public static string LogPath
        {
            get
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Performix", "logs");
                return Path.Combine(root, "update.log");
            }
        }

        /// <summary>
        /// Locates invoke-update.ps1: output\scripts, output root, then — only for a build output
        /// directory (a "...\bin\..." path) — the repository's scripts\ folder a few levels up.
        ///
        /// WHY THE WALK IS RESTRICTED: in a deployed install (C:\Program Files\Performix) an
        /// ancestor with a scripts\invoke-update.ps1 would be attacker-writable in exactly the
        /// places a normal user can write (their profile, a folder they unzipped to). Executing a
        /// script found there would be a local privilege/execution hole. Only a development
        /// layout has a "bin" segment, so only there is the walk attempted.
        /// </summary>
        public static string? ResolveScript() => ResolveScript(AppContext.BaseDirectory);

        /// <summary>Testable form of <see cref="ResolveScript()"/>; never walks up outside bin\ layouts.</summary>
        internal static string? ResolveScript(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

            // 1. The script that ships next to the exe. This is the production path.
            string deployed = Path.Combine(baseDirectory, "scripts", ScriptName);
            if (File.Exists(deployed)) return deployed;

            string beside = Path.Combine(baseDirectory, ScriptName);
            if (File.Exists(beside)) return beside;

            // 2. Development layout only: bin\x64\Release\<tfm>\<rid>\ → walk up looking for
            // scripts\. A deployed directory has no "bin" segment, so it never reaches here.
            if (!IsBuildOutputLayout(baseDirectory)) return null;

            DirectoryInfo? directory = new(baseDirectory);
            for (int depth = 0; depth < 8 && directory is not null; depth++)
            {
                string candidate = Path.Combine(directory.FullName, "scripts", ScriptName);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>True for a path under a "bin" directory (build output), not an install directory.</summary>
        private static bool IsBuildOutputLayout(string baseDirectory)
        {
            string[] segments = baseDirectory.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Absolute path to Windows PowerShell. A bare "powershell.exe" is resolved by Windows
        /// through the application directory and the current directory before PATH, so an exe
        /// dropped next to Performix.exe could be started instead. %SystemRoot%\System32 is not
        /// user-writable, so pinning it removes that hijack.
        /// </summary>
        public static string PowershellPath
        {
            get
            {
                string systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
                    ?? Environment.GetEnvironmentVariable("windir")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            }
        }

        /// <summary>
        /// Starts the replacement script with the verified package. Returns a failure instead of
        /// throwing so the page can show the reason; nothing has been modified at that point.
        ///
        /// THE STATE MACHINE HAS THE LAST WORD: a package may only be installed from
        /// <see cref="UpdatePageState.ReadyToInstall"/>, i.e. after the download was verified.
        /// Without this check an install started from a failed page (the "换源重试" bug) could
        /// swap the program while the card still showed a failure and no progress.
        ///
        /// <paramref name="machine"/> also supplies the version being replaced. It travels to the
        /// restarted build as "-RestartArguments --updated-from &lt;version&gt;", which is how the
        /// new process knows it just updated and can say so (UpdateLaunchNotice).
        /// </summary>
        public static UpdateInstallStart Start(string packagePath, UpdateAsset asset, UpdateStateMachine machine)
        {
            if (machine is null) throw new ArgumentNullException(nameof(machine));

            if (machine.State != UpdatePageState.ReadyToInstall)
            {
                return new UpdateInstallStart(false,
                    "The update is not verified and ready to install, so the replacement script was not started.",
                    $"install refused: state={machine.State} stage={machine.FailureStage}", null, LogPath);
            }

            string? script = ResolveScript();
            if (script is null)
            {
                return new UpdateInstallStart(false,
                    "The update replacement script (scripts\\invoke-update.ps1) is missing next to Performix.exe.",
                    appContextHint(), null, LogPath);
            }

            if (!File.Exists(packagePath))
            {
                return new UpdateInstallStart(false, "The verified update package is gone.", packagePath, script, LogPath);
            }

            string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string log = LogPath;
            string restartArguments = BuildRestartArguments(machine.CurrentVersionText);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] app: starting {ScriptName} for " +
                    $"{asset.Name} ({asset.SizeBytes} bytes) pid={Environment.ProcessId} " +
                    $"restartArguments='{restartArguments}'\r\n",
                    Encoding.UTF8);
            }
            catch { /* the script logs on its own; a locked log must not stop the update */ }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    // Absolute path, never a bare name (see PowershellPath).
                    FileName = PowershellPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                };
                foreach (string argument in BuildScriptArguments(script, packagePath, target,
                             Environment.ProcessId, asset.Sha256, machine.CurrentVersionText))
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process? process = Process.Start(startInfo);
                if (process is null)
                    return new UpdateInstallStart(false, "The replacement script did not start.", script, script, log);

                ShellLog.Write("update",
                    $"spawned powershell -File {script} -Target {target} -Pid {Environment.ProcessId} " +
                    $"-Sha256 {asset.Sha256[..8]}… -RestartArguments '{restartArguments}' (script log {log})");

                return new UpdateInstallStart(true, null, null, script, log);
            }
            catch (Exception ex)
            {
                return new UpdateInstallStart(false, "The replacement script could not be started: " + ex.Message,
                    ex.GetType().Name, script, log);
            }
        }

        /// <summary>
        /// The command line handed to powershell.exe, in order. Split out so a test can prove the
        /// version hand-off is actually transmitted without spawning powershell.
        /// </summary>
        internal static IReadOnlyList<string> BuildScriptArguments(string script, string packagePath,
            string target, int processId, string sha256, string? currentVersion)
        {
            var arguments = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-WindowStyle", "Hidden",
                "-File", script,
                "-Zip", packagePath,
                "-Target", target,
                "-Pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-Sha256", sha256,
            };

            // The script only appends this to the restarted process when it is non-empty, so an
            // unreadable running version simply means no notice (never an empty argument).
            string restartArguments = BuildRestartArguments(currentVersion);
            if (restartArguments.Length > 0)
            {
                arguments.Add("-RestartArguments");
                arguments.Add(restartArguments);
            }

            return arguments;
        }

        /// <summary>"--updated-from 0.1.0-alpha.1", or empty when the version is unknown.</summary>
        internal static string BuildRestartArguments(string? currentVersion)
            => string.IsNullOrWhiteSpace(currentVersion)
                ? string.Empty
                : UpdateLaunchNotice.UpdatedFromSwitch + " " + currentVersion.Trim();

        private static string appContextHint()
            => "looked in: " + Path.Combine(AppContext.BaseDirectory, "scripts") + ", " +
               AppContext.BaseDirectory;
    }
}
