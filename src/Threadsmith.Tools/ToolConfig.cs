namespace Threadsmith.Tools;

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

/// <summary>Reads bounded tool-specific values from layered host configuration.</summary>
public interface IToolConfig
{
    /// <summary>Gets a typed tool setting or a caller-provided default.</summary>
    T Get<T>(string toolId, string key, T defaultValue);

    /// <summary>Gets every scalar setting declared for one tool.</summary>
    IReadOnlyDictionary<string, string> GetAll(string toolId);
}

/// <summary>Microsoft.Extensions.Configuration-backed tool configuration.</summary>
public sealed class ToolConfig : IToolConfig
{
    private readonly IConfiguration _configuration;

    /// <summary>Initializes a new instance of the <see cref="ToolConfig"/> class.</summary>
    public ToolConfig(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <inheritdoc />
    public T Get<T>(string toolId, string key, T defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _configuration[$"tools:config:{toolId}:{key}"];
        if (value is null)
        {
            return defaultValue;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)value;
        }

        TypeConverter converter = TypeDescriptor.GetConverter(typeof(T));
        if (!converter.CanConvertFrom(typeof(string)))
        {
            throw new InvalidOperationException(
                $"Tool configuration '{toolId}:{key}' cannot be converted to {typeof(T).Name}.");
        }

        try
        {
            var converted = converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
            return converted is T typed
                ? typed
                : throw new InvalidOperationException(
                    $"Tool configuration '{toolId}:{key}' produced no {typeof(T).Name} value.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Tool configuration '{toolId}:{key}' is not a valid {typeof(T).Name} value.",
                exception);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetAll(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return _configuration
            .GetSection($"tools:config:{toolId}")
            .GetChildren()
            .Where(child => child.Value is not null)
            .ToDictionary(
                child => child.Key,
                child => child.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }
}
