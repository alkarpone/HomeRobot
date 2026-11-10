using System.Text.RegularExpressions;

namespace HomeRobot.Server.AI;

internal static class RobotVocabulary
{
    public static readonly IReadOnlyDictionary<string, string[]> Phrases = new Dictionary<string, string[]>
    {
        ["move_forward"] = ["jeď dopředu", "jeď vpřed", "jet dopředu", "jet vpřed", "dopředu", "popojeď", "popojeď dopředu", "popojeď vpřed", "posuň se dopředu", "posunout se dopředu", "rozjeď se", "pokračuj dopředu", "jeď rovně", "vpřed"],
        ["move_backward"] = ["jeď dozadu", "jeď zpátky", "jet dozadu", "jet zpátky", "couvni", "couvnout", "zacouvej", "zacouvat", "zpátky", "dozadu", "vrať se", "popojeď dozadu", "popojeď zpátky", "posuň se dozadu", "posunout se dozadu"],
        ["rotate_left"] = ["otoč se doleva", "otoč se vlevo", "se otoč doleva", "zatoč doleva", "odboč doleva", "vlevo", "otočit doleva", "otočit se doleva", "otočit se vlevo", "otočit vlevo"],
        ["rotate_right"] = ["otoč se doprava", "otoč se vpravo", "se otoč doprava", "zatoč doprava", "odboč doprava", "vpravo", "otočit doprava", "otočit se doprava", "otočit se vpravo", "otočit vpravo"],
        ["rotation_unspecified"] = ["otoč se", "otoč se dokola", "udělej čelem vzad"],
        ["stop"] = ["stop", "zastav", "zastav se", "stůj", "okamžitě zastav", "nouzové zastavení", "přestaň jet", "nehýbej se", "zůstaň stát"],
        ["speed"] = ["zpomal", "zrychli", "pomaleji", "rychleji", "jeď pomalu", "jeď rychle", "nastav rychlost", "sniž rychlost", "zvyš rychlost"],
        ["quantity"] = ["centimetr", "centimetry", "metr", "metry", "milimetr", "kousek", "trochu", "půl metru", "jeden metr", "deset centimetrů", "devadesát stupňů", "čtvrt otáčky", "půl otáčky", "celá otáčka"],
        ["navigation"] = ["přijeď ke mně", "následuj mě", "jeď za mnou", "vrať se na místo", "vrať se domů", "jeď do kuchyně", "vrať se do kuchyně", "jeď do obýváku", "objeď překážku", "vyhni se překážce", "zastav před překážkou", "otoč se ke mně", "najdi cestu", "zaparkuj", "přijeď blíž", "odjeď dál"],
        ["camera"] = ["otoč hlavu", "podívej se doleva", "podívej se doprava", "podívej se nahoru", "podívej se dolů", "podívej se na mě", "sleduj mě", "zapni kameru", "vypni kameru", "vyfoť to", "udělej fotku", "rozhlédni se"],
        ["happy"] = ["usměj se", "se usměj", "usmát se", "usmát", "buď veselý", "ukaž radost"],
        ["sad"] = ["buď smutný", "zamrač se", "zamračit se", "zamračit"],
        ["display"] = ["mrkni", "zavři oči", "otevři oči", "ukaž překvapení", "změň výraz", "ukaž obličej", "zhasni displej", "zapni displej", "nastav jas"],
        ["audio"] = ["mluv hlasitěji", "mluv tišeji", "ztiš se", "zvyš hlasitost", "sniž hlasitost", "přestaň mluvit", "zopakuj to", "řekni to znovu", "mluv pomaleji", "mluv rychleji", "poslouchej", "přestaň poslouchat", "zapni mikrofon", "vypni mikrofon"],
        ["hardware"] = ["zapni světla", "vypni světla", "rozsvit", "zhasni", "změň barvu", "nastav modrou", "nastav červenou", "nastav zelenou", "zkontroluj baterii", "kolik máš baterie", "jaký máš stav", "restartuj se"],
        ["conversation"] = ["ahoj", "jak se máš", "jak se jmenuješ", "co umíš", "co právě děláš", "děkuji", "dobrou noc", "probuď se", "pamatuj si", "zapomeň"]
    };

    private static readonly (Regex Pattern, string Replacement)[] Corrections =
    [
        (Word("do prava"), "doprava"),
        (Word("do leva"), "doleva"),
        (Word("odbols"), "odboč"),
        (Word("popojet"), "popojeď"),
        (Word("otoď"), "otoč"),
        (Word("stupnů"), "stupňů")
    ];

    public static string Normalize(string text, bool commandWords = true)
    {
        text = Regex.Replace(text.Trim().Normalize(), @"\s+", " ");
        foreach (var (pattern, replacement) in (commandWords ? Corrections : Corrections[..2]))
            text = pattern.Replace(text, match => char.IsUpper(match.Value[0])
                ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement);
        return text;
    }

    public static Regex Word(string phrase) =>
        new(@"\b" + Regex.Escape(phrase) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
