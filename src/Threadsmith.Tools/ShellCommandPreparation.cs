namespace Threadsmith.Tools;

using System.Collections.Immutable;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using static Threadsmith.Policies.Contracts.CommandPolicyNames;

/// <summary>Contains one immutable, host-analyzed shell admission and its exact launch.</summary>
internal sealed record PreparedShellCommand(
    string OriginalCommand,
    string WorkingDirectory,
    string Shell,
    string Identity,
    ImmutableArray<CommandFacts> Actions,
    PreparedProcessLaunch Launch);

/// <summary>Parses a deliberately bounded literal-command language, not a full shell dialect.</summary>
internal static class ShellCommandPreparation
{
    /// <summary>Gets the code-owned denial reason, rendered only at the feedback boundary.</summary>
    internal const string DenialReasonCode = "CommandExecutionDenied";

    /// <summary>Analyzes and binds every command before any part can execute.</summary>
    internal static PreparedShellCommand Prepare(string command, string shellName, string workingDirectory, IReadOnlyDictionary<string, string> additions)
    {
        var shell = Path.GetFileNameWithoutExtension(shellName).ToLowerInvariant();
        var platform = OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? MacOS : Linux;
        if (shell is not (WindowsPowerShell or PowerShell or Cmd or Bash or Sh or Zsh)
            || (!OperatingSystem.IsWindows() && shell is Cmd or WindowsPowerShell))
        {
            throw Denied();
        }

        var environment = ProcessLaunchResolver.EnvironmentFor(additions);
        if (OperatingSystem.IsWindows() && shell is Bash or Sh or Zsh)
        {
            // MSYS conversion runs after shell quoting; freeze literal native arguments before authorization.
            environment = environment.SetItem("MSYS2_ARG_CONV_EXCL", "*").SetItem("MSYS_NO_PATHCONV", "1");
        }

        var resolver = new ProcessLaunchResolver(environment);
        var shellPath = resolver.Resolve(shellName);
        ValidateInterpreter(shell, shellPath, environment);

        var parsed = Parse(command, shell);
        var text = new StringBuilder();
        var actions = ImmutableArray.CreateBuilder<CommandFacts>();
        foreach (var item in parsed)
        {
            var name = item.Tokens[0];
            var directScript = IsScript(name);
            string target;
            var shellCommand = false;
            try
            {
                var interpreter = directScript
                    ? IsPowerShellScript(name)
                        ? shell is WindowsPowerShell or PowerShell ? shell : PowerShell
                        : shell is Bash or Sh or Zsh ? shell : Bash
                    : null;
                target = interpreter == shell ? shellPath : resolver.Resolve(interpreter ?? name);
            }
            catch (FileNotFoundException) when (!directScript && IsShellName(name))
            {
                // Name permission, not a claim that a particular module or builtin implements it.
                target = string.Empty;
                shellCommand = true;
            }

            if (shellCommand && parsed.Length > 1)
            {
                // A name permission cannot establish shell-state effects on later actions.
                throw Denied();
            }

            if (!shellCommand && OperatingSystem.IsWindows() && !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw Denied();
            }

            var executableName = shellCommand ? name : OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(target) : Path.GetFileName(target);
            var interpreterName = executableName.ToLowerInvariant();
            var scriptCall = directScript || interpreterName is WindowsPowerShell or PowerShell or Bash or Sh or Zsh
                ? BindScript(item, directScript, interpreterName, target, workingDirectory, environment)
                : null;
            if (interpreterName == Cmd)
            {
                // Unanalyzed nested command text is still unsupported.
                throw Denied();
            }

            if (shellCommand && shell is WindowsPowerShell or PowerShell && item.HasUnquotedComma)
            {
                // Cmdlet comma arrays are not literal argv; do not silently turn them into strings.
                throw Denied();
            }

            var arguments = scriptCall?.InterpreterArguments ?? [.. item.Tokens.Skip(1)];
            var invocationKind = shellCommand ? ShellCommand : !directScript && Path.IsPathFullyQualified(name) ? ExplicitExternal : External;
            actions.Add(new CommandFacts(platform, shell, executableName, target, invocationKind, arguments, workingDirectory));
            if (scriptCall is not null)
            {
                actions.Add(new CommandFacts(platform, shell, Path.GetFileName(scriptCall.Path), scriptCall.Path, Script, scriptCall.ScriptArguments, workingDirectory));
            }

            if (shell is WindowsPowerShell or PowerShell)
            {
                text.Append("& ");
            }

            var shellTarget = shellCommand ? name : OperatingSystem.IsWindows() && shell is Bash or Sh or Zsh ? target.Replace('\\', '/') : target;
            text.Append(shellCommand && shell == Cmd ? shellTarget : Quote(shellTarget, shell, native: !shellCommand));
            if (shellCommand && shell == Cmd)
            {
                // CMD builtins consume command text, not native argv. Retain admitted quoting.
                text.Append(item.RawArguments);
            }
            else
            {
                for (var index = 0; index < arguments.Length; index++)
                {
                    var argument = arguments[index];
                    text.Append(' ').Append(shellCommand && item.NamedParameters.Contains(index + 1)
                        ? QuoteParameter(argument, shell) : Quote(argument, shell, native: !shellCommand));
                }
            }

            text.Append(item.Separator);
        }

        var preparedText = text.ToString();
        ImmutableArray<string> startup = shell switch
        {
            WindowsPowerShell or PowerShell => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", preparedText],
            Bash => ["--noprofile", "--norc", "-c", preparedText],
            Zsh => ["-f", "-c", preparedText],
            Sh => ["-c", preparedText],
            Cmd => ["/d", "/s", "/c", preparedText],
            _ => throw Denied(),
        };
        var rawCommandLine = shell == Cmd ? "/d /s /c \"" + preparedText + "\"" : null;
        var launch = new PreparedProcessLaunch(shellPath, startup, environment, rawCommandLine);
        var identity = PolicyJson.Fingerprint(new { command, workingDirectory, shell, launch.FileName, launch.Arguments, launch.CommandLine, Environment = environment.OrderBy(pair => pair.Key, StringComparer.Ordinal), Actions = actions.ToImmutable() });
        return new PreparedShellCommand(command, workingDirectory, shell, identity, actions.ToImmutable(), launch);
    }

    /// <summary>Parses each dialect's admitted static literal subset without asking a shell to evaluate input.</summary>
    internal static ImmutableArray<LiteralCommand> Parse(string command, string shell)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768 || command.Contains('\0'))
        {
            throw Denied();
        }

        var powershell = shell is WindowsPowerShell or PowerShell;
        var cmd = shell == Cmd;
        var result = ImmutableArray.CreateBuilder<LiteralCommand>();
        var tokens = new List<string>();
        var token = new StringBuilder();
        var started = false;
        var explicitCall = false;
        var unquotedComma = false;
        var parameter = false;
        var parameters = ImmutableHashSet.CreateBuilder<int>();
        var quote = '\0';
        var argumentStart = 0;
        var index = 0;
        for (; index < command.Length; index++)
        {
            var character = command[index];
            if (cmd && character is '%' or '!' or '^' or '\r' or '\n')
            {
                throw Denied();
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (powershell && quote == '\'' && index + 1 < command.Length && command[index + 1] == '\'')
                    {
                        token.Append('\'');
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                else
                {
                    if (quote == '"' && ((powershell && character is '$' or '`') || (!powershell && !cmd && character is '$' or '`' or '\\')))
                    {
                        throw Denied();
                    }

                    token.Append(character);
                }

                continue;
            }

            if (character == '"' || (!cmd && character == '\''))
            {
                if (powershell && tokens.Count == 0 && !started && !explicitCall)
                {
                    throw Denied();
                }

                quote = character;
                started = true;
                continue;
            }

            if (!cmd && !powershell && character == '\\')
            {
                if (++index == command.Length || command[index] is '\r' or '\n')
                {
                    throw Denied();
                }

                token.Append(command[index]);
                started = true;
                continue;
            }

            if (!cmd && character == '#' && !started)
            {
                while (index < command.Length && command[index] != '\n')
                {
                    index++;
                }

                character = '\n';
            }

            if (character is '<' or '>' or '(' or ')' or '{' or '}' || (!cmd && character is '$' or '`' or '@' or '*' or '?' or '[' or ']'))
            {
                throw Denied();
            }

            if (!cmd && character == '~' && (!started || (!powershell && token.Length > 0 && token[^1] is '=' or ':')))
            {
                // Leading home expansion (and POSIX assignment/path-list expansion) is not literal.
                throw Denied();
            }

            unquotedComma |= powershell && character == ',';
            var separator = character is '|' or '&' || (!cmd && character is ';' or '\n' or '\r');
            if (separator)
            {
                if (powershell && character == '&' && !started && tokens.Count == 0)
                {
                    if (index + 1 < command.Length && command[index + 1] == '&')
                    {
                        throw Denied();
                    }

                    explicitCall = true;
                    continue;
                }

                FinishToken();
                if (tokens.Count == 0)
                {
                    if (character is '\r' or '\n')
                    {
                        continue;
                    }

                    throw Denied();
                }

                var argumentEnd = index;
                var operation = character.ToString();
                if (character is '|' or '&' && index + 1 < command.Length && command[index + 1] == character)
                {
                    operation += character;
                    index++;
                }

                if ((powershell && (operation == "&" || (shell == WindowsPowerShell && operation is "&&" or "||"))) || (!cmd && !powershell && operation == "&"))
                {
                    throw Denied();
                }

                if (operation is "\r" or "\n")
                {
                    operation = ";";
                }

                result.Add(new LiteralCommand([.. tokens], " " + operation + " ", parameters.ToImmutable(), command[argumentStart..argumentEnd], unquotedComma));
                tokens.Clear();
                parameters.Clear();
                unquotedComma = false;
                explicitCall = false;
                if (result.Count > 64)
                {
                    throw Denied();
                }
            }
            else if (char.IsWhiteSpace(character))
            {
                FinishToken();
            }
            else
            {
                if (!started)
                {
                    parameter = powershell && character == '-';
                }

                token.Append(character);
                started = true;
            }
        }

        if (quote != '\0')
        {
            throw Denied();
        }

        FinishToken();
        if (tokens.Count > 0)
        {
            result.Add(new LiteralCommand([.. tokens], string.Empty, parameters.ToImmutable(), command[argumentStart..], unquotedComma));
        }
        else if (result.Count == 0 || result[^1].Separator.Contains('|') || result[^1].Separator.Contains('&'))
        {
            throw Denied();
        }

        return result.ToImmutable();

        void FinishToken()
        {
            if (!started)
            {
                return;
            }

            var value = token.ToString();
            if (powershell && value == "--%")
            {
                // PowerShell consumes even a quoted stop-parsing token before native argument binding.
                throw Denied();
            }

            if (tokens.Count == 0)
            {
                argumentStart = index;
            }

            if (parameter && (value == "--" || (value.Length > 1 && char.IsAsciiLetter(value[1]))))
            {
                parameters.Add(tokens.Count);
            }

            tokens.Add(value);
            if (tokens.Count > 1025)
            {
                throw Denied();
            }

            token.Clear();
            started = false;
            parameter = false;
        }
    }

    private static bool IsPowerShellScript(string path) => Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string path) => IsPowerShellScript(path) || Path.GetExtension(path).Equals(".sh", StringComparison.OrdinalIgnoreCase);

    private static void ValidateInterpreter(string interpreter, string target, IReadOnlyDictionary<string, string> environment)
    {
        if ((!OperatingSystem.IsWindows() && interpreter is Cmd or WindowsPowerShell)
            || (OperatingSystem.IsWindows() && !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw Denied();
        }

        if (OperatingSystem.IsWindows() && interpreter is Bash or Sh or Zsh
            && environment.TryGetValue("SYSTEMROOT", out var systemRoot)
            && target.StartsWith(Path.TrimEndingDirectorySeparator(systemRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // WSL wrappers do not bind Windows native executable or script identities.
            throw Denied();
        }
    }

    private static ScriptInvocation BindScript(LiteralCommand item, bool direct, string interpreter, string target, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        ValidateInterpreter(interpreter, target, environment);
        var powershell = interpreter is WindowsPowerShell or PowerShell;
        var index = 1;
        var options = ImmutableArray.CreateBuilder<string>();
        if (!direct)
        {
            if (powershell)
            {
                while (index < item.Tokens.Length && !item.Tokens[index].Equals("-File", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.Tokens[index].ToLowerInvariant() is not ("-nologo" or "-noprofile" or "-noninteractive"))
                    {
                        throw Denied();
                    }

                    index++;
                }

                if (index == item.Tokens.Length)
                {
                    throw Denied();
                }

                index++;
            }
            else
            {
                while (index < item.Tokens.Length && item.Tokens[index].StartsWith('-'))
                {
                    var option = item.Tokens[index++];
                    if (option == "--")
                    {
                        break;
                    }

                    if (option is "-e" or "-u" or "-x" or "-v" or "-f"
                        || (interpreter == Bash && option is "--noprofile" or "--norc"))
                    {
                        options.Add(option);
                    }
                    else
                    {
                        throw Denied();
                    }
                }
            }
        }

        if (!direct && index >= item.Tokens.Length)
        {
            throw Denied();
        }

        var operand = direct ? item.Tokens[0] : item.Tokens[index++];
        if (!IsScript(operand) || powershell != IsPowerShellScript(operand))
        {
            throw Denied();
        }

        var path = Path.GetFullPath(operand, workingDirectory);
        if (!File.Exists(path))
        {
            throw Denied();
        }

        ImmutableArray<string> arguments = [.. item.Tokens.Skip(index)];
        var scriptOperand = OperatingSystem.IsWindows() && !powershell ? path.Replace('\\', '/') : path;
        ImmutableArray<string> startup = interpreter switch
        {
            WindowsPowerShell or PowerShell => ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", path, .. arguments],
            Bash => ["--noprofile", "--norc", .. options, "--", scriptOperand, .. arguments],
            Zsh => ["-f", .. options, "--", scriptOperand, .. arguments],
            Sh => [.. options, "--", scriptOperand, .. arguments],
            _ => throw Denied(),
        };
        return new ScriptInvocation(path, arguments, startup);
    }

    private sealed record ScriptInvocation(string Path, ImmutableArray<string> ScriptArguments, ImmutableArray<string> InterpreterArguments);

    private static bool IsShellName(string name) => name.Length > 0 && char.IsAsciiLetter(name[0])
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string QuoteParameter(string value, string shell)
    {
        // Preserve generic PowerShell parameter syntax, not command-specific parameter binding.
        var colon = value.IndexOf(':');
        var name = colon < 0 ? value : value[..colon];
        if (name.Length < 2 || !char.IsAsciiLetter(name[1])
            || !name.Skip(2).All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw Denied();
        }

        return colon < 0 ? name : name + ":" + Quote(value[(colon + 1)..], shell, native: false);
    }

    private static string Quote(string value, string shell, bool native = true)
    {
        if (shell == Cmd)
        {
            if (value.EndsWith('\\') || value.IndexOfAny(['"', '%', '!', '^', '\r', '\n']) >= 0)
            {
                throw Denied();
            }

            return "\"" + value + "\"";
        }

        if (shell is WindowsPowerShell or PowerShell)
        {
            // Native argument marshalling differs across these engines. Reject unverified forms.
            if (native && (value.Length == 0 || value.Contains('"') || value.EndsWith('\\')))
            {
                throw Denied();
            }

            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static UnauthorizedAccessException Denied() => new(DenialReasonCode);

    /// <summary>Contains literal tokens and a validated sequence operator.</summary>
    internal sealed record LiteralCommand(ImmutableArray<string> Tokens, string Separator, ImmutableHashSet<int> NamedParameters, string RawArguments, bool HasUnquotedComma);
}
