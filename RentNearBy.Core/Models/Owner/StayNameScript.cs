namespace RentNearBy.Core.Models.Owner;

// S10: a stay name where one word mixes Latin letters with Cyrillic or Greek letters (look-alike spoofing, e.g. "Таj").
// Only a warning chip for the admin; the name is never refused for it.
public static class StayNameScript
{
    private enum Script { None, Latin, Cyrillic, Greek }

    public static bool IsMixed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var word in name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var latin = false;
            var other = false;
            foreach (var c in word)
            {
                switch (Of(c))
                {
                    case Script.Latin: latin = true; break;
                    case Script.Cyrillic or Script.Greek: other = true; break;
                }
                if (latin && other) return true;
            }
        }
        return false;
    }

    private static Script Of(char c)
    {
        if (!char.IsLetter(c)) return Script.None;
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || c is >= 'À' and <= 'ɏ') return Script.Latin;
        if (c is >= 'Ѐ' and <= 'ԯ') return Script.Cyrillic;
        if (c is >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿') return Script.Greek;
        return Script.None;
    }
}
