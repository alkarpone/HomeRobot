using System.Text.RegularExpressions;

namespace HomeRobot.Server.Memory;

public sealed class MemoryCommands(MemoryStore store, IMemoryRetrieval retrieval, TimeProvider clock)
{
    private static readonly Regex Prefix = new(
        @"^(?:(?:prosim(?: te)?|robote)[, ]+)*(?<verb>pamatuj si|zapamatuj si|uloz si|zmen si|zapomen|smaz)(?:[, ]+(?<target>.+))?$");
    private static readonly Regex Confirm = new(@"^potvrzuji smazani(?: pameti)?$");
    private static readonly Regex List = new(@"^co si o mne pamatujes$|^co (?:mas|je) (?:o mne )?ulozen[oe]$");
    private static readonly Regex Bulk = new(@"^(?:vse|vsechno|veskerou pamet|celou pamet|vsechny (?:moje )?(?:ulozene )?(?:vzpominky|zaznamy|informace|preference|projekty|rutiny|osobni udaje))$");

    internal static bool IsExplicit(string text) => Prefix.IsMatch(IntentText(text)) ||
        Confirm.IsMatch(IntentText(text)) || List.IsMatch(IntentText(text));

    internal string? Handle(string text, Guid session, bool local, bool clearSpeech, ref PendingDeletion? pending)
    {
        var intent = IntentText(text);
        var previous = pending;
        pending = null;

        if (Confirm.IsMatch(intent))
        {
            if (!local)
                return "Uloženou paměť lze spravovat jen přímo na serveru.";
            if (!clearSpeech || text.TrimEnd().EndsWith('?'))
                return "Potvrzení jsem nerozuměl. Mazání zadej prosím znovu.";
            if (previous is null || previous.Expires <= clock.GetUtcNow())
                return "Nemám žádné čekající mazání.";
            return store.DeleteAll(previous.Category, previous.Fingerprint)
                ? "Potvrzené záznamy jsem smazal."
                : "Záznamy se mezitím změnily. Zadej mazání znovu.";
        }

        if (List.IsMatch(intent))
        {
            if (!local)
                return "Uloženou paměť lze číst jen přímo na serveru.";
            var entries = store.List();
            return entries.Count == 0 ? "Nemám žádné uložené vzpomínky." :
                $"Uložené záznamy ({entries.Count}): {string.Join("; ", entries.Take(3).Select(entry => entry.Content))}";
        }

        var match = Prefix.Match(intent);
        if (!match.Success)
            return null;
        if (!local)
            return "Uloženou paměť lze spravovat jen přímo na serveru.";
        if (!clearSpeech)
            return "Nerozuměl jsem dost přesně. Zopakuj prosím požadavek.";
        if (text.TrimEnd().EndsWith('?'))
            return "Řekni prosím přímo, co mám uložit, změnit nebo smazat.";

        // Match the folded form for routing and the original form for the saved Czech text.
        var target = match.Groups["target"];
        if (!target.Success)
            return "Upřesni prosím, které informace máš na mysli.";
        var original = text.Trim().Normalize();
        var start = 0;
        while (start < original.Length && MemoryStore.Fold(original[..start]).Length < target.Index)
            start++;
        var content = Regex.Replace(original[start..].Trim(), @"^že\s+", "", RegexOptions.IgnoreCase);
        var verb = match.Groups["verb"].Value;
        var category = MemoryRetrieval.Category(content);

        if (verb is "pamatuj si" or "zapamatuj si" or "uloz si")
        {
            if (!MemoryStore.ValidContent(content))
                return "Tento údaj nemohu uložit.";
            return store.Create(content, category ?? "other", "conversation", session) is null
                ? "Záznam už existuje nebo je paměť plná."
                : "Uložil jsem si to.";
        }

        if ((verb is "zapomen" or "smaz") && Bulk.IsMatch(target.Value))
        {
            var rows = store.Snapshot(category);
            if (rows.Length == 0)
                return "V této skupině nejsou žádné uložené záznamy.";
            pending = new(category, MemoryStore.Fingerprint(rows), clock.GetUtcNow().AddMinutes(2));
            return $"Smazat {rows.Length} záznamů? Pokud ano, řekni: Potvrzuji smazání.";
        }

        // Unrecognized bulk phrasing must never fall through to a single deletion.
        if (Regex.IsMatch(target.Value, @"\b(?:vse|vseho|vsech[a-z]*|vesker[a-z]*|celou|cely|hromadne)\b"))
            return "Upřesni prosím skupinu záznamů, kterou chceš smazat.";

        var query = content;
        if (verb == "zmen si")
        {
            var correction = Regex.Split(content, @",\s*ne\s+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (correction.Length == 2)
            {
                content = correction[0].Trim();
                query = correction[1];
            }
            else if (category is not null)
                query = Regex.Replace(content, @"\b(?:se jmenuje|se jmenují)\b.*$", "", RegexOptions.IgnoreCase);
            if (!MemoryStore.ValidContent(content))
                return "Tento údaj nemohu uložit.";
        }

        var candidates = MemoryRetrieval.MatchForMutation(query, store.Snapshot(category)).Take(2).ToArray();
        if (candidates.Length != 1)
            return "Upřesni prosím jeden konkrétní uložený záznam.";
        var candidate = candidates[0];
        var changed = verb == "zmen si"
            ? store.Update(candidate.Entry.Id, content, category ?? candidate.Entry.Category, candidate.Revision)
            : store.Delete(candidate.Entry.Id, candidate.Revision);
        return changed ? verb == "zmen si" ? "Uložený údaj jsem změnil." : "Uložený údaj jsem smazal."
            : "Změnu se nepodařilo uložit. Zkontroluj prosím záznam.";
    }

    internal string? Recall(string text, bool local)
    {
        if (!local || !Regex.IsMatch(IntentText(text), @"^(?:jak se jmenuje|co vis o|jakou hudbu mam rad)\b"))
            return null;
        var relevant = retrieval.Retrieve(text);
        return relevant.Count == 1 ? $"Mám uložené: {relevant[0].Content}" : null;
    }

    private static string IntentText(string text) =>
        MemoryStore.Fold(text.Trim()).TrimEnd('.', '!', '?').TrimEnd();

    internal sealed record PendingDeletion(string? Category, string Fingerprint, DateTimeOffset Expires);
}
