using System.Runtime.Versioning;

// PerformixSetup is a Windows-only executable: it probes %ProgramFiles%\dotnet, talks to
// IShellLink through COM and starts an MSI bootstrapper. Declaring it at the assembly level is
// what lets the Windows-only BCL calls (Marshal, Type.GetTypeFromCLSID, Registry-free advapi32
// P/Invoke) be used from a plain net10.0 target framework without a CA1416 warning per call —
// and, importantly, without switching the project to net10.0-windows, which would drag the
// WindowsDesktop framework reference into an installer that exists to install that framework.
[assembly: SupportedOSPlatform("windows")]
