using System.Reflection;

namespace BubblesBot.Research.Probing;

/// <summary>
/// Discovers every <see cref="IProbe"/> in this assembly by reflection. Drop a new probe file
/// under <c>Probes/</c> with a parameterless constructor and it is found automatically — no central
/// list to edit.
/// </summary>
public static class ProbeRegistry
{
    private static readonly Lazy<IReadOnlyList<IProbe>> Probes = new(Discover);

    public static IReadOnlyList<IProbe> All => Probes.Value;

    public static IProbe? ByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<IProbe> ByGroup(string group) =>
        All.Where(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<IProbe> Discover()
    {
        var list = new List<IProbe>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IProbe).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            list.Add((IProbe)Activator.CreateInstance(type)!);
        }
        return list.OrderBy(p => p.Group, StringComparer.Ordinal)
                   .ThenBy(p => p.Name, StringComparer.Ordinal)
                   .ToList();
    }
}
