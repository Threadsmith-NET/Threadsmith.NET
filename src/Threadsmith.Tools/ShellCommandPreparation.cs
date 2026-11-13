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

/// <summary>Identifies named commands for policy while preserving variable, argument, and redirection syntax.</summary>
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
            text.Append(item.AssignmentPrefix);
            if (item.IsAssignment)
            {
                text.Append(item.Separator);
                continue;
            }

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

            if (!shellCommand && OperatingSystem.IsWindows() && !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw Denied();
            }

            var executableName = shellCommand ? name : OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(target) : Path.GetFileName(target);
            var interpreterName = executableName.ToLowerInvariant();
            var scriptCall = directScript || interpreterName is WindowsPowerShell or PowerShell or Bash or Sh or Zsh
                ? BindScript(item, directScript, interpreterName, target, workingDirectory, environment, shell)
                : null;
            if (interpreterName == Cmd)
            {
                // Unanalyzed nested command text is still unsupported.
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
            if (scriptCall is null)
            {
                text.Append(item.RawArguments);
            }
            else
            {
                text.Append(scriptCall.LaunchArguments);
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

    /// <summary>Parses named commands and variable setup without asking a shell to evaluate input.</summary>
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
        var tokenEnds = new List<int>();
        var tokenStarts = new List<int>();
        var tokenExpansions = new List<bool>();
        var tokenHasExpansion = false;
        var tokenStart = 0;
        var commentStart = -1;
        var redirectTarget = false;
        var quote = '\0';
        var index = 0;
        for (; index < command.Length; index++)
        {
            var character = command[index];
            if (cmd && character is '^' or '\r' or '\n')
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
                    if (quote == '"' && ((!cmd && character == '$') || (cmd && character is '%' or '!')))
                    {
                        var end = ReadVariableEnd(command, index, shell);
                        token.Append(command[index..end]);
                        index = end - 1;
                        tokenHasExpansion = true;
                        continue;
                    }

                    if (quote == '"' && ((powershell && character == '`') || (!powershell && !cmd && character is '`' or '\\')))
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

                if (!started)
                {
                    tokenStart = index;
                }

                quote = character;
                started = true;
                continue;
            }

            if (!cmd && !powershell && character == '\\')
            {
                if (!started)
                {
                    tokenStart = index;
                }

                if (++index == command.Length || command[index] is '\r' or '\n')
                {
                    throw Denied();
                }

                token.Append(command[index]);
                started = true;
                continue;
            }

            if ((!cmd && character == '$') || (cmd && character is '%' or '!'))
            {
                if (!started)
                {
                    tokenStart = index;
                }

                var end = ReadVariableEnd(command, index, shell);
                token.Append(command[index..end]);
                index = end - 1;
                started = true;
                tokenHasExpansion = true;
                continue;
            }

            if (!cmd && character == '#' && !started)
            {
                commentStart = index;
                while (index < command.Length && command[index] != '\n')
                {
                    index++;
                }

                character = '\n';
            }

            if (character is '<' or '>' || (powershell && character == '*' && index + 1 < command.Length && command[index + 1] == '>'))
            {
                if (started && token.Length > 0 && command[tokenStart..index].All(char.IsAsciiDigit))
                {
                    // A stream number adjacent to the operator is not a command argument.
                    token.Clear();
                    started = false;
                }

                FinishToken();
                if (tokens.Count == 0 || redirectTarget)
                {
                    throw Denied();
                }

                if (character == '*')
                {
                    character = command[++index];
                }

                if (index + 1 < command.Length && command[index + 1] == character)
                {
                    if (character == '<')
                    {
                        throw Denied();
                    }

                    index++;
                }

                if (index + 1 < command.Length && command[index + 1] == '&')
                {
                    index++;
                }

                redirectTarget = true;
                continue;
            }

            if (character is '(' or ')' or '{' or '}' || (!cmd && character is '`' or '@' or '*' or '?' or '[' or ']'))
            {
                throw Denied();
            }

            if (!cmd && character == '~' && (!started || (!powershell && token.Length > 0 && token[^1] is '=' or ':')))
            {
                // Leading home expansion (and POSIX assignment/path-list expansion) is not literal.
                throw Denied();
            }

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
                if (redirectTarget)
                {
                    throw Denied();
                }

                if (tokens.Count == 0)
                {
                    if (character is '\r' or '\n')
                    {
                        commentStart = -1;
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

                FinishCommand(argumentEnd, " " + operation + " ");
                tokens.Clear();
                tokenEnds.Clear();
                tokenStarts.Clear();
                tokenExpansions.Clear();
                commentStart = -1;
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
                    tokenStart = index;
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
        if (redirectTarget)
        {
            throw Denied();
        }

        if (tokens.Count > 0)
        {
            FinishCommand(command.Length, string.Empty);
        }
        else if (result.Count == 0 || result[^1].Separator.Contains('|') || result[^1].Separator.Contains('&'))
        {
            throw Denied();
        }

        return result.ToImmutable();

        void FinishCommand(int end, string separator)
        {
            var raw = command[tokenStarts[0]..end];
            if (powershell && raw.StartsWith('$') && raw.IndexOf('=') is > 0 and var equals)
            {
                var variable = raw[..equals].TrimEnd();
                var variableName = variable.TrimStart('$', '{').TrimEnd('}');
                var scopeEnd = variableName.IndexOf(':');
                var valueEnd = commentStart < 0 ? raw.Length : commentStart - tokenStarts[0];
                if (ReadVariableEnd(variable, 0, shell) != variable.Length
                    || (scopeEnd >= 0 && variableName[..scopeEnd].ToLowerInvariant() is not ("env" or "global" or "local" or "script" or "private" or "variable"))
                    || !IsScalarAssignmentValue(raw[(equals + 1)..valueEnd].Trim(), shell))
                {
                    throw Denied();
                }

                result.Add(new LiteralCommand([], separator, string.Empty, [], [], raw, true, []));
                return;
            }

            var first = 0;
            if (!powershell && !cmd)
            {
                while (first < tokens.Count && IsPosixAssignment(command[tokenStarts[first]..tokenEnds[first]]))
                {
                    first++;
                }
            }

            var assignmentOnly = first == tokens.Count;
            if (!assignmentOnly && tokenExpansions[first])
            {
                // Policy still requires a statically named command, even when arguments expand.
                throw Denied();
            }

            var prefix = first == 0 ? string.Empty : command[tokenStarts[0]..(assignmentOnly ? end : tokenStarts[first])];
            var argumentsStart = assignmentOnly ? end : tokenEnds[first];
            result.Add(new LiteralCommand(
                [.. tokens.Skip(first)],
                separator,
                command[argumentsStart..end],
                [.. tokenEnds.Skip(first).Select(tokenEnd => tokenEnd - argumentsStart)],
                [.. tokenStarts.Skip(first).Select(start => start - argumentsStart)],
                prefix,
                assignmentOnly,
                [.. tokenExpansions.Skip(first)]));
        }

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

            if (redirectTarget)
            {
                redirectTarget = false;
            }
            else
            {
                tokens.Add(value);
                tokenEnds.Add(index);
                tokenStarts.Add(tokenStart);
                tokenExpansions.Add(tokenHasExpansion);
            }

            if (tokens.Count > 1025)
            {
                throw Denied();
            }

            token.Clear();
            started = false;
            tokenHasExpansion = false;
        }
    }

    private static int ReadVariableEnd(string text, int start, string shell)
    {
        var cmd = shell == Cmd;
        var powershell = shell is WindowsPowerShell or PowerShell;
        var index = start + 1;
        var closing = cmd ? text[start] : index < text.Length && text[index] == '{' ? '}' : '\0';
        if (!cmd && closing != '\0')
        {
            index++;
        }

        var nameStart = index;
        if (closing != '\0' && (cmd || powershell))
        {
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or ':' or '-' or '.' or ' ' or '(' or ')'))
            {
                index++;
            }

            if (index == nameStart || index == text.Length || text[index] != closing)
            {
                throw Denied();
            }

            return index + 1;
        }

        if (!cmd && closing == '\0' && index < text.Length
            && (powershell ? text[index] is '?' or '$' or '^' : text[index] is '?' or '$' or '#' or '@' or '*' or '!' or '-' || char.IsAsciiDigit(text[index])))
        {
            return index + 1;
        }

        if (index == text.Length || !(char.IsLetter(text[index]) || text[index] == '_'))
        {
            throw Denied();
        }

        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_' || (powershell && text[index] == ':')))
        {
            index++;
        }

        if (index == nameStart || (closing != '\0' && (index == text.Length || text[index++] != closing)))
        {
            throw Denied();
        }

        return index;
    }

    private static bool IsScalarAssignmentValue(string value, string shell)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == '$')
        {
            return ReadVariableEnd(value, 0, shell) == value.Length;
        }

        if (value[0] is not ('\'' or '"'))
        {
            return decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        var quote = value[0];
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != quote)
            {
                continue;
            }

            if (quote == '\'' && index + 1 < value.Length && value[index + 1] == quote)
            {
                index++;
                continue;
            }

            return index == value.Length - 1;
        }

        return false;
    }

    private static bool IsPosixAssignment(string token)
    {
        var equals = token.IndexOf('=');
        return equals > 0 && (char.IsAsciiLetter(token[0]) || token[0] == '_')
            && token.AsSpan(1, equals - 1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".AsSpan()) < 0;
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

    private static ScriptInvocation BindScript(LiteralCommand item, bool direct, string interpreter, string target, string workingDirectory, IReadOnlyDictionary<string, string> environment, string outerShell)
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
        if (item.TokenExpansions[direct ? 0 : index - 1] || !IsScript(operand) || powershell != IsPowerShellScript(operand))
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
        var requiredStartup = interpreter switch
        {
            WindowsPowerShell or PowerShell => " -NoLogo -NoProfile -NonInteractive",
            Bash => " --noprofile --norc",
            Zsh => " -f",
            _ => string.Empty,
        };
        var launchArguments = direct
            ? " " + string.Join(' ', startup.Take(startup.Length - arguments.Length).Select(argument => Quote(argument, outerShell))) + item.RawArguments
            : requiredStartup + item.RawArguments[..item.TokenStarts[index - 1]] + Quote(scriptOperand, outerShell) + item.RawArguments[item.TokenEnds[index - 1]..];
        return new ScriptInvocation(path, arguments, startup, launchArguments);
    }

    private sealed record ScriptInvocation(string Path, ImmutableArray<string> ScriptArguments, ImmutableArray<string> InterpreterArguments, string LaunchArguments);

    private static bool IsShellName(string name) => name.Length > 0 && char.IsAsciiLetter(name[0])
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

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

    /// <summary>Contains source tokens, variable setup, and a validated sequence operator.</summary>
    internal sealed record LiteralCommand(
        ImmutableArray<string> Tokens,
        string Separator,
        string RawArguments,
        ImmutableArray<int> TokenEnds,
        ImmutableArray<int> TokenStarts,
        string AssignmentPrefix,
        bool IsAssignment,
        ImmutableArray<bool> TokenExpansions);
}
