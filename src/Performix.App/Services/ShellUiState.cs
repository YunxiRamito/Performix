// Tiny persisted UI state for the WinUI shell.
//
// Why a separate file: the expanded/collapsed home flag must survive a restart, but
// preysense.json is a red line (no writes from this stage) and is also shared with the
// in-production WPF build, whose schema this project must not extend. So the shell keeps
// its own file under %LOCALAPPDATA%\Predex\shell-ui.json.
//
// Failure is silent on purpose: if the file cannot be read or written the shell simply
// starts collapsed.

using System;
using System.IO;
using System.Text.Json;

namespace Predex.WinUI.Services
{
    internal sealed class ShellUiState
    {
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Predex");

        private static string FilePath => Path.Combine(DirectoryPath, "shell-ui.json");

        public bool HomeExpanded { get; set; }

        public static ShellUiState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new ShellUiState();
                var state = JsonSerializer.Deserialize<ShellUiState>(File.ReadAllText(FilePath));
                return state ?? new ShellUiState();
            }
            catch
            {
                return new ShellUiState();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                ShellLog.Write("state", $"saved homeExpanded={HomeExpanded} → {FilePath}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("state", $"save failed: {ex.Message}");
            }
        }
    }
}
