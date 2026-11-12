namespace Threadsmith.Policies.Contracts;

/// <summary>Defines stable host fact and profile identifiers shared by policy consumers.</summary>
public static class CommandPolicyNames
{
    /// <summary>The command action domain.</summary>
    public const string Domain = "command";

    /// <summary>The Windows platform profile.</summary>
    public const string Windows = "windows";

    /// <summary>The macOS platform profile.</summary>
    public const string MacOS = "macos";

    /// <summary>The Linux platform profile.</summary>
    public const string Linux = "linux";

    /// <summary>The Windows PowerShell dialect.</summary>
    public const string WindowsPowerShell = "powershell";

    /// <summary>The PowerShell Core dialect.</summary>
    public const string PowerShell = "pwsh";

    /// <summary>The Windows command interpreter dialect.</summary>
    public const string Cmd = "cmd";

    /// <summary>The Bash dialect.</summary>
    public const string Bash = "bash";

    /// <summary>The POSIX shell dialect.</summary>
    public const string Sh = "sh";

    /// <summary>The Zsh dialect.</summary>
    public const string Zsh = "zsh";

    /// <summary>An absent supported shell profile.</summary>
    public const string UnsupportedShell = "unsupported";

    /// <summary>An external command selected by name.</summary>
    public const string External = "external";

    /// <summary>An external command selected by explicit absolute path.</summary>
    public const string ExplicitExternal = "explicit-external";

    /// <summary>A literal shell command name whose implementation is not pinned to a file.</summary>
    public const string ShellCommand = "shell-command";

    /// <summary>A script file bound to an independently authorized interpreter.</summary>
    public const string Script = "script";
}
