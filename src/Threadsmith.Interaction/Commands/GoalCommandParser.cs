namespace Threadsmith.Interaction.Commands;

using System.Text;
using Threadsmith.Core;

/// <summary>Parses the argument after the shared router has recognized /goal.</summary>
internal static class GoalCommandParser
{
    /// <summary>Shared syntax for command discovery and parse errors.</summary>
    internal const string Usage = "/goal | /goal cancel | /goal set \"<goal>\"";

    /// <summary>Returns a session control only if the entire argument is valid.</summary>
    internal static SessionGoalCommand? Parse(SessionId sessionId, string argument)
    {
        if (argument.Length == 0)
        {
            return new(sessionId, SessionGoalAction.Show);
        }

        if (argument.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return new(sessionId, SessionGoalAction.Cancel);
        }

        if (argument.Length < 6 || !argument.StartsWith("set", StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(argument[3]))
        {
            return null;
        }

        var quoted = argument.AsSpan(4).TrimStart();
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"' || quoted.Length > (2 * SessionGoalCommand.MaximumGoalLength) + 2)
        {
            return null;
        }

        var text = new StringBuilder();
        for (var i = 1; i < quoted.Length; i++)
        {
            var value = quoted[i];
            if (value == '"')
            {
                if (i != quoted.Length - 1)
                {
                    return null;
                }

                var goal = text.ToString();
                return string.IsNullOrWhiteSpace(goal) ? null : new(sessionId, SessionGoalAction.Set, goal);
            }

            if (value == '\\' && quoted[i + 1] is '"' or '\\')
            {
                value = quoted[++i];
            }

            if (text.Length == SessionGoalCommand.MaximumGoalLength)
            {
                return null;
            }

            text.Append(value);
        }

        return null;
    }
}
