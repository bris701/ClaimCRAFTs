namespace ClaimCRAFTs.Models;

public sealed record CraftCategory(string Name, string Section, int Order, long[] FunctionIds);

public static class CraftCategories
{
    // Game-wide station/function IDs, not claim-specific building IDs.
    private static readonly CraftCategory[] Entries =
    [
        new("Forestry", "Professions", 0, [44]),
        new("Carpentry", "Professions", 1, [20]),
        new("Scholar", "Professions", 2, [48]),
        new("Mining", "Professions", 3, [635094930]),
        new("Smithing", "Professions", 4, [24, 25]),
        new("Masonry", "Professions", 5, [21, 22]),
        new("Foraging", "Professions", 6, [2012420824]),
        new("Farming", "Professions", 7, [33]),
        new("Tailoring", "Professions", 8, [14, 17]),
        new("Hunting", "Professions", 9, [1559722792]),
        new("Leatherworking", "Professions", 10, [15, 16]),
        new("Fishing", "Professions", 11, [40]),
        new("Cooking", "Skills", 12, [12, 13, 200031066]),
        new("Sailing", "Skills", 13, [1837107818]),
        new("Taming", "Skills", 14, [104950060, 1680198173]),
        new("Construction", "Skills", 15, [937867074])
    ];

    private static readonly CraftCategory Unknown = new("Other / Unknown", "Other", 99, []);

    public static CraftCategory For(CraftProject project) =>
        Entries.FirstOrDefault(category => project.FunctionIds.Any(id => category.FunctionIds.Contains(id))) ?? Unknown;

    public static IEnumerable<IGrouping<CraftCategory, CraftProject>> Group(IEnumerable<CraftProject> projects) =>
        projects.GroupBy(For).OrderBy(group => group.Key.Order);
}
