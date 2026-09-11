namespace Threadsmith.Interaction.Agents;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Immutable, validated display names. Names never affect execution roles or prompts.</summary>
public sealed class AgentNameCatalog
{
    /// <summary>Maximum entries accepted per explicit list.</summary>
    public const int MaximumNames = 128;

    /// <summary>Maximum UTF-16 units in a normalized display name.</summary>
    public const int MaximumNameLength = 32;

    private static readonly FrozenDictionary<AgentRole, IReadOnlyList<string>> Defaults = new Dictionary<AgentRole, IReadOnlyList<string>>
    {
        [AgentRole.Explorer] = Array.AsReadOnly<string>(["Amundsen", "Cousteau", "Humboldt", "Magellan", "Shackleton", "Zheng He"]),
        [AgentRole.Implementer] = Array.AsReadOnly<string>(["Lovelace", "Hopper", "Thompson", "Ritchie", "Hamilton", "Liskov"]),
        [AgentRole.SecurityReviewer] = Array.AsReadOnly<string>(["Turing", "Shannon", "Diffie", "Hellman", "Rivest", "Shamir"]),
        [AgentRole.TestReviewer] = Array.AsReadOnly<string>(["Dijkstra", "Hoare", "Myers", "Hamming", "Knuth", "Hopper"]),
        [AgentRole.PerformanceReviewer] = Array.AsReadOnly<string>(["Amdahl", "Gustafson", "Cray", "Hennessy", "Patterson", "Knuth"]),
        [AgentRole.ArchitectureReviewer] = Array.AsReadOnly<string>(["Brooks", "Parnas", "Kay", "Dijkstra", "Liskov", "Shaw"]),
    }.ToFrozenDictionary();

    private readonly FrozenDictionary<AgentRole, IReadOnlyList<string>> _names;

    /// <summary>Initializes a new instance of the <see cref="AgentNameCatalog"/> class.</summary>
    public AgentNameCatalog(IReadOnlyList<string>? defaultNames = null, IReadOnlyDictionary<AgentRole, IReadOnlyList<string>>? byRole = null)
    {
        var shared = Validate(defaultNames);
        _names = Enum.GetValues<AgentRole>().ToFrozenDictionary(role => role, role =>
        {
            var explicitNames = byRole is not null && byRole.TryGetValue(role, out var names) ? Validate(names) : [];
            return explicitNames.Count > 0 ? explicitNames : shared.Count > 0 ? shared : Defaults[role];
        });
    }

    /// <summary>Gets the immutable effective list for a public role.</summary>
    public IReadOnlyList<string> GetNames(AgentRole role) => _names[role];

    /// <summary>Normalizes printable names and rejects unusable entries without making configuration fatal.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string>? names)
    {
        var accepted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (names ?? []).Take(MaximumNames))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumNameLength * 2 || raw.Any(char.IsControl))
            {
                continue;
            }

            string name;
            try
            {
                name = raw.Trim().Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (name.Length is 0 or > MaximumNameLength || name.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
                    or UnicodeCategory.OtherNotAssigned))
            {
                continue;
            }

            if (seen.Add(name))
            {
                accepted.Add(name);
            }
        }

        return accepted.AsReadOnly();
    }
}

/// <summary>Atomically reserves unique active names, including configured suffix collisions.</summary>
public sealed class AgentNameAllocator
{
    private readonly Lock _gate = new();
    private readonly AgentNameCatalog _catalog;
    private readonly Random _random;
    private readonly Dictionary<AgentPresentationTarget, string> _assigned = [];
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="AgentNameAllocator"/> class.</summary>
    public AgentNameAllocator(AgentNameCatalog catalog, Random? random = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _random = random ?? Random.Shared;
    }

    /// <summary>Returns the same name until the exact assignment is released.</summary>
    public string Allocate(AgentPresentationTarget target, AgentRole role)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (_assigned.TryGetValue(target, out var existing))
            {
                return existing;
            }

            var names = _catalog.GetNames(role);
            var available = names.Where(name => !_reserved.Contains(name)).ToArray();
            var selected = available.Length > 0 ? available[_random.Next(available.Length)] : names[_random.Next(names.Count)];
            if (available.Length == 0)
            {
                var suffix = 1;
                while (_reserved.Contains(selected + "_" + suffix.ToString(CultureInfo.InvariantCulture)))
                {
                    suffix++;
                }

                selected += "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            _reserved.Add(selected);
            _assigned.Add(target, selected);
            return selected;
        }
    }

    /// <summary>Releases only the retired assignment without renaming survivors.</summary>
    public void Release(AgentPresentationTarget target)
    {
        lock (_gate)
        {
            if (_assigned.Remove(target, out var name))
            {
                _reserved.Remove(name);
            }
        }
    }
}
