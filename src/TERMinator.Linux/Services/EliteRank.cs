namespace TERMinator.Linux.Services;

/// <summary>Callsign rank earned from the total number of successful BBS calls (AppSettings.TotalCalls).</summary>
public static class EliteRank
{
    private static readonly (int MinCalls, string Name)[] Ranks =
    [
        (0, "LAMER"),
        (5, "USER"),
        (20, "HACKER"),
        (50, "ELITE"),
        (150, "SYSOP")
    ];

    /// <summary>Every rank, lowest first, with the calls it takes.</summary>
    public static IReadOnlyList<(int MinCalls, string Name)> All => Ranks;

    public static string NameFor(int calls) => Ranks.Last(rank => calls >= rank.MinCalls).Name;

    /// <summary>The next rank and the total calls it needs, or null at the top rank.</summary>
    public static (string Name, int Calls)? NextFor(int calls)
    {
        foreach (var (minCalls, name) in Ranks)
        {
            if (minCalls > calls)
                return (name, minCalls);
        }
        return null;
    }
}
