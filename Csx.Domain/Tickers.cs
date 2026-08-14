namespace Csx.Domain;

public static class Tickers
{
    public static readonly IReadOnlyDictionary<string, char> TierLetters = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["Premier"] = 'P',
        ["Elite"] = 'E',
        ["Challenger"] = 'C',
        ["Contender"] = 'N',
        ["Prospect"] = 'S',
        ["Recruit"] = 'R'
    };

    public static string FromPrefixAndTier(string? prefix, string? tierName, long teamId)
    {
        var p = Sanitize(prefix);
        if (p.Length is < 2 or > 4)
            p = "T" + teamId.ToString("D3");

        var letter = tierName is not null && TierLetters.TryGetValue(tierName, out var c)
            ? c
            : 'X';

        var ticker = (p + letter).ToUpperInvariant();
        if (ticker.Length > 5)
            ticker = ticker[..5];
        return ticker;
    }

    public static string LineKey(string? prefix, string? tierName)
    {
        var p = Sanitize(prefix);
        var t = (tierName ?? "").Trim();
        return $"{p}|{t}";
    }

    private static string Sanitize(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "";
        var chars = prefix.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToUpperInvariant();
    }
}

public static class SignedPlayerTypes
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIGNED",
        "SIGNEDPROMOTED",
        "SIGNEDSUBBED",
        "SIGNEDPROMOTEDSUBBED",
        "TEMPSIGNED",
        "PERMFATEMPSIGNED",
        "IR",
        "IRP"
    };

    public static bool IsRostered(string? type) =>
        type is not null && All.Contains(type.Replace("_", "").Replace(" ", ""));
}
