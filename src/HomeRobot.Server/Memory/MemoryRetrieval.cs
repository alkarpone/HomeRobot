using System.Text.RegularExpressions;

namespace HomeRobot.Server.Memory;

public interface IMemoryRetrieval
{
    IReadOnlyList<MemoryEntry> Retrieve(string query);
}

internal sealed class MemoryRetrieval(MemoryStore store) : IMemoryRetrieval
{
    private static readonly (string Category, string Pattern)[] Topics =
    [
        ("people_pets", @"\b(?:pes|ps[a-z]+|pejsk[a-z]*|kock[a-z]*|kocour[a-z]*|lide|osob[a-z]*)\b"),
        ("preferences", @"\b(?:rad|rada|libi|preferenc[a-z]*|preferuj[a-z]*|obliben[a-z]*|hudb[a-z]*)\b"),
        ("projects", @"\bprojekt[a-z]*\b"),
        ("routines", @"\b(?:kazdodenn[a-z]*|rutin[a-z]*|obvykle|pravidelne)\b"),
        ("personal", @"\b(?:bydlim|bydlist[a-z]*|narodil[a-z]*|narozen[a-z]*|osobni udaje)\b")
    ];
    private static readonly string[] MusicGenres = ["drumandbass", "jazz", "rock", "metal", "techno"];
    private static readonly HashSet<string> StopWords =
        ["jak", "jakou", "povez", "neco", "tom", "tim", "tam", "mne", "mym", "mou", "sve", "sveho", "rada", "jmenuje", "jmenem", "muj", "mem", "moje", "mam", "jsem", "rad", "rika", "rikal",
         "pamatujes", "pamatuj", "zapomen", "ulozene", "vsechny", "vis", "jake", "jaky",
         "ktery", "ktera", "ktere", "mohl", "prosim", "jako", "nebo", "ktereho", "ulozenych"];

    internal static string? Category(string text) =>
        Topics.FirstOrDefault(topic => Regex.IsMatch(MemoryStore.Fold(text), topic.Pattern)).Category;

    public IReadOnlyList<MemoryEntry> Retrieve(string query)
    {
        var terms = QueryTerms(query);
        return terms.Count == 0 ? [] : store.Search(Category(query), content => Score(terms, content));
    }

    internal static IEnumerable<MemoryStore.SnapshotRow> MatchForMutation(
        string query, IReadOnlyList<MemoryStore.SnapshotRow> rows)
    {
        var terms = QueryTerms(query);
        return terms.Count == 0 ? [] : rows.Where(row => Score(terms, row.Entry.Content) == terms.Count);
    }

    private static int Score(HashSet<string> terms, string content) => Terms(content).Count(terms.Contains);

    private static HashSet<string> QueryTerms(string query)
    {
        var terms = Terms(query);
        if (terms.Overlaps(MusicGenres))
            terms.Remove("hudba");
        return terms;
    }

    private static HashSet<string> Terms(string text)
    {
        var folded = Regex.Replace(MemoryStore.Fold(text), @"\b(?:drum\s+(?:and|&)\s+bass|dnb)\b", "drumandbass");
        var terms = Regex.Matches(folded, "[a-z0-9]+").Select(match => match.Value)
            .Where(term => !StopWords.Contains(term))
            .Select(term => term switch
            {
                "pes" or "psa" or "psovi" or "psem" or "pejsek" or "pejska" => "pes",
                _ when term.StartsWith("hudb", StringComparison.Ordinal) => "hudba",
                _ when term.StartsWith("projekt", StringComparison.Ordinal) => "projekt",
                _ when term.Length >= 3 => term,
                _ => ""
            }).Where(term => term.Length != 0).ToHashSet();

        if (terms.Overlaps(MusicGenres))
            terms.Add("hudba");
        return terms;
    }
}
