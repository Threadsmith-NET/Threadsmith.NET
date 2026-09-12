namespace Threadsmith.Models.OpenAiCompatible;

using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>Holds a possible orphaned XML argument tail until native tool calls can disambiguate it.</summary>
internal sealed class NativeToolTextGuard
{
    private const string ParameterStart = "<parameter";
    private readonly StringBuilder _pending = new();
    private bool _passThrough;
    private bool _parameterCandidate;

    /// <summary>Streams ordinary content immediately; only a leading parameter fragment is deferred.</summary>
    internal string Append(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_passThrough)
        {
            return content;
        }

        _pending.Append(content);
        if (_parameterCandidate)
        {
            return string.Empty;
        }

        var pending = _pending.ToString();
        var trimmed = pending.AsSpan().TrimStart();
        if (ParameterStart.AsSpan().StartsWith(trimmed, StringComparison.Ordinal)
            || (trimmed.StartsWith(ParameterStart, StringComparison.Ordinal)
                && trimmed.Length > ParameterStart.Length
                && char.IsWhiteSpace(trimmed[ParameterStart.Length])))
        {
            _parameterCandidate = trimmed.Length > ParameterStart.Length;
            return string.Empty;
        }

        _pending.Clear();
        _passThrough = true;
        return pending;
    }

    /// <summary>Returns deferred content unchanged unless native calls accompany a confirmed orphaned tail.</summary>
    internal bool Complete(bool hasNativeToolCalls, out string content)
    {
        content = _pending.ToString();
        _pending.Clear();
        _passThrough = true;
        if (!hasNativeToolCalls || !IsOrphanedParameterTail(content))
        {
            return false;
        }

        content = string.Empty;
        return true;
    }

    private static bool IsOrphanedParameterTail(string text)
    {
        if (!text.AsSpan().TrimEnd().EndsWith("</tool_call>", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            // A tail supplies closing function/tool tags without their opening tags. Parsing with
            // just those missing parents distinguishes it from ordinary parameter XML and prose.
            using var input = new StringReader("<tool_call><function>" + text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var root = XElement.Load(reader);
            var functions = root.Elements().ToArray();
            if (functions.Length != 1 || functions[0].Name != "function")
            {
                return false;
            }

            var parameters = functions[0].Elements().ToArray();
            return parameters.Length > 0
                && parameters.All(parameter => parameter.Name == "parameter"
                    && !string.IsNullOrWhiteSpace((string?)parameter.Attribute("name")))
                && root.Nodes().OfType<XText>().All(node => string.IsNullOrWhiteSpace(node.Value))
                && functions[0].Nodes().OfType<XText>().All(node => string.IsNullOrWhiteSpace(node.Value));
        }
        catch (XmlException)
        {
            // Unconfirmed markup remains normal content, including XML/code examples.
            return false;
        }
    }
}
