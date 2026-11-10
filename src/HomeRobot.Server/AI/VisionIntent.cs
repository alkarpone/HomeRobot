using System.Text.RegularExpressions;

namespace HomeRobot.Server.AI;

internal static class VisionIntent
{
    private static readonly Regex Movement = new(
        @"\b(?:otoč|pohni|natoč|nakloň)\b.*\b(?:kamer\p{L}*|hlav\p{L}*)\b|^podívej se (?:vlevo|vpravo|do stran)\b",
        RegexOptions.CultureInvariant);

    private const string Objects =
        @"(?:ps\p{L}*|pes|kočk\p{L}*|lid\p{L}*|člověk\p{L}*|osob\p{L}*|židl\p{L}*|stůl|stol\p{L}*|překážk\p{L}*|auto|auta|notebook\p{L}*|předmět\p{L}*|dveře|okn\p{L}*|text|něco)";

    private static readonly Regex Fresh = new(
        @"\A(?:rozhlédni se|podívej se znovu|co (?:teď )?vidíš(?: (?:teď|před sebou|kolem sebe))?|podívej se(?: před sebe)?|co je (?:na obrázku|na stole|přede mnou)|co (?:mám|držím) v ruce|popiš(?:,)? (?:co vidíš|co je před tebou|místnost|obrázek)|vidíš (?:nějak\p{L}+ )?" + Objects + @"(?: na obrázku)?|kolik (?:vidíš [\p{L} ]+|je tam " + Objects + @")|je na obrázku [\p{L} ]+|je tam (?:nějak\p{L}+ )?" + Objects + @"|přečti (?:mi )?(?:ten )?text(?: na obrázku|,? který vidíš)?|co je tohle|poznáš (?:ten )?předmět)\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex FollowUp = new(
        @"\A(?:jakou má barvu|jakou to má barvu|jakou barvu má ten předmět|jakou barvu má (?:ten|ta|to|tento|tahle) [\p{L} ]+|kolik jich (?:tam )?je|a jakou má barvu)\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex Past = new(
        @"\A(?:co (?:jsi )?viděl(?: předtím| minule)?|co bylo na (?:předchozím|posledním) snímku)\z",
        RegexOptions.CultureInvariant);

    public static string? Route(string text) => Movement.IsMatch(text) ? "vision_unsupported" :
        Past.IsMatch(text) ? "vision_past" : FollowUp.IsMatch(text) ? "vision_followup" : Fresh.IsMatch(text) ? "vision" : null;
}
