using System.Globalization;
using System.Text.RegularExpressions;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Speech;

namespace HomeRobot.Server.AI;

internal sealed record VoiceIntent(string Text, string Route, string? Intent = null, string? Reason = null,
    IReadOnlyList<RobotCommand>? Actions = null)
{
    private static readonly string[] Movements = ["move_forward", "move_backward", "rotate_left", "rotate_right"];
    private static readonly string[] Unsupported = ["navigation", "camera", "speed", "display", "audio", "hardware"];
    private static readonly Regex Candidate = new(@"\b(?:" + string.Join("|",
        RobotVocabulary.Phrases.Where(group => group.Key is not ("quantity" or "conversation"))
            .SelectMany(group => group.Value).Select(Regex.Escape)) +
        @"|ne(?:jezd|otáč|zastav|couvej|popoj|hýb)\p{L}*|jeď|otoč|odboč|zatoč|posuň)\b");
    private static readonly Regex Prefix = new(@"\A(?:(?:robote|prosím tě|prosím),? |(?<request>mohl by ses|mohl bys|můžeš se|můžeš) )+");
    // A new imperative can mark the next step even when the transcript has no conjunction.
    private static readonly Regex Sequence = new(
        @",? (?:a pak|a potom|potom|pak|a) |(?<!\bse)[,.]? (?=(?:jeď|popojeď|posuň|pokračuj|couvni|zacouvej|otoč|odboč|zatoč)\b)");
    private const string SpeedAmount = @"(?<amount>[\p{L} ]+|\d+(?:[.,]\d+)?)";
    private static readonly Regex Speed = new(@",? (?:rychlostí|na|při rychlosti|tempem) " + SpeedAmount + @"\s*(?:%|procent(?:a|o)?)\z");
    private static readonly Regex PercentSpeed = new(@",? " + SpeedAmount + @"[ -]?procentní rychlostí\z");
    private static readonly Dictionary<string, double> Numbers = CzechNumbers();
    private static readonly Regex Quantity = new(
        @"(?:^| )(?:(?:o )?(?<number>[0-9]+(?:[.,][0-9]+)?|" + string.Join("|", Numbers.Keys.OrderByDescending(key => key.Length)) + @")\s*(?<unit>cm|mm|m|centimetr(?:ů|y)?|milimetr(?:ů|y)?|metr(?:ů|y|u)?|°|stup(?:eň|ně|ňů)|otáčky)|(?<full>celá otáčka))(?= |$)");

    public static VoiceIntent Analyze(SpeechTranscript transcript, double defaultSpeed)
    {
        var raw = transcript.Text;
        if (raw.Length > 2000 || !raw.Any(char.IsLetter) || raw.Any(char.IsControl))
            return new(raw, "invalid");

        var normalized = RobotVocabulary.Normalize(raw);
        var isQuestion = normalized.TrimEnd('.', '!').EndsWith('?');
        var text = normalized.ToLowerInvariant().TrimEnd('.', '!', '?').TrimEnd();
        var prefix = Prefix.Match(text);
        var command = text[prefix.Length..];
        if (!isQuestion && RobotVocabulary.Phrases["stop"].Contains(command))
            return transcript.ClearSpeech ? new(normalized, "stop", "stop") :
                new(normalized, "unclear", "stop", "Whisper did not report clear speech.");

        var visualRoute = VisionIntent.Route(command);
        if (visualRoute is not null)
            return transcript.ClearSpeech ? new(normalized, visualRoute, "vision") :
                new(normalized, "unclear", "vision", "Whisper did not report clear speech.");

        var clauses = Sequence.Split(command);
        if (clauses.Length > 5)
            return new(normalized, "ambiguous", Reason: "Use at most five actions.");

        var results = clauses.Select(clause => AnalyzeClause(clause, normalized, defaultSpeed)).ToArray();
        if (results.All(result => result.Route == "conversation"))
            return new(RobotVocabulary.Normalize(raw, commandWords: false), "conversation", "conversation");
        var rejection = results.FirstOrDefault(result => result.Actions is null && result.Route != "conversation");
        if (rejection is not null)
            return rejection;
        if (results.Any(result => result.Actions is null))
            return new(normalized, "ambiguous", Reason: "The full sequence could not be verified.");

        var actions = results.Select(result => result.Actions![0]).ToArray();
        if (isQuestion && !prefix.Groups["request"].Success)
            return new(normalized, "ambiguous", Reason: "Please state the command explicitly.");

        var intent = actions.Length == 1 ? actions[0].Command : "sequence";
        return transcript.ClearSpeech
            ? new(normalized, "action", intent, Actions: actions)
            : new(normalized, "unclear", intent, "Whisper did not report clear speech.");
    }

    private static VoiceIntent AnalyzeClause(string clause, string text, double defaultSpeed)
    {
        var unsupported = Unsupported.FirstOrDefault(intent => RobotVocabulary.Phrases[intent]
            .Any(phrase => clause == phrase || clause.StartsWith(phrase + " ", StringComparison.Ordinal)));
        if (unsupported is not null)
            return new(text, "unsupported", unsupported, "This command is unavailable.");

        foreach (var expression in new[] { "happy", "sad" })
            if (RobotVocabulary.Phrases[expression].Contains(clause))
                return new(text, "action", "set_expression", Actions: [new("set_expression", new ExpressionParameters(expression))]);

        var speedMatch = Speed.Match(clause);
        if (!speedMatch.Success)
            speedMatch = PercentSpeed.Match(clause);
        var movementText = speedMatch.Success ? clause[..speedMatch.Index] : clause;
        var quantities = Quantity.Matches(movementText);
        // The quantity may follow the verb or the direction. Match the complete remaining phrase.
        foreach (Match amount in quantities)
        {
            var phrase = movementText.Remove(amount.Index, amount.Length).Trim();
            var movement = Movement(phrase);
            if (movement is null)
                continue;

            var unit = amount.Groups["unit"].Value;
            var angle = unit is "°" or "otáčky" || unit.StartsWith("stup", StringComparison.Ordinal) ||
                amount.Groups["full"].Success;
            var rotation = movement.StartsWith("rotate", StringComparison.Ordinal);
            if (angle != rotation)
                return new(text, "clarification_required", movement, rotation ? "Specify an angle." : "Specify a distance.");

            if (phrase is "popojeď" or "rozjeď se" or "vrať se")
                return new(text, "clarification_required", "movement_unspecified", "Specify a direction.");

            var speed = speedMatch.Success ? ReadNumber(speedMatch.Groups["amount"].Value.Trim()) : defaultSpeed;
            if (speed is null)
                return new(text, "clarification_required", movement, "Specify a speed in percent.");

            var scale = unit switch
            {
                "otáčky" => 360,
                "mm" => 0.1,
                "m" => 100,
                _ when unit.StartsWith("metr", StringComparison.Ordinal) => 100,
                _ when unit.StartsWith("milimetr", StringComparison.Ordinal) => 0.1,
                _ => 1
            };
            var number = amount.Groups["full"].Success ? 360 : ReadNumber(amount.Groups["number"].Value);
            if (number is null)
                return new(text, "ambiguous", movement, "Invalid quantity.");
            var value = scale == 0.1 ? number.Value / 10 : number.Value * scale;
            RobotCommand action = rotation
                ? new(movement, new RotateParameters(value, speed.Value))
                : new(movement, new MoveParameters(value, speed.Value));
            return new(text, "action", movement, Actions: [action]);
        }

        var intent = Movement(movementText);
        if (intent is not null)
            return new(text, "clarification_required", intent,
                intent.StartsWith("rotate", StringComparison.Ordinal) ? "Missing rotation angle." : "Missing movement distance.");
        if (RobotVocabulary.Phrases["rotation_unspecified"].Any(phrase => clause == phrase ||
            clause.StartsWith(phrase + " ", StringComparison.Ordinal)))
            return new(text, "clarification_required", "rotation_unspecified", "Specify a direction, angle and speed.");

        return Candidate.IsMatch(clause)
            ? new(text, "ambiguous", Reason: "Negated, incomplete or ambiguous command.")
            : new(text, "conversation", "conversation");
    }

    private static string? Movement(string phrase) =>
        Movements.FirstOrDefault(intent => RobotVocabulary.Phrases[intent].Contains(phrase));

    private static double? ReadNumber(string text) =>
        Numbers.TryGetValue(text, out var value) ? value :
        double.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value) ? value : null;

    private static Dictionary<string, double> CzechNumbers()
    {
        string[] units = ["", "jeden", "dva", "tři", "čtyři", "pět", "šest", "sedm", "osm", "devět",
            "deset", "jedenáct", "dvanáct", "třináct", "čtrnáct", "patnáct", "šestnáct", "sedmnáct", "osmnáct", "devatenáct"];
        string[] tens = ["", "", "dvacet", "třicet", "čtyřicet", "padesát", "šedesát", "sedmdesát", "osmdesát", "devadesát"];
        var numbers = new Dictionary<string, double> { ["nula"] = 0, ["jedna"] = 1, ["dvě"] = 2,
            ["půl"] = 0.5, ["čtvrt"] = 0.25, ["sto"] = 100 };
        for (var value = 1; value < 100; value++)
        {
            var phrase = value < 20 ? units[value] : (tens[value / 10] + " " + units[value % 10]).TrimEnd();
            numbers.Add(phrase, value);
            if (value <= 80)
                numbers.Add("sto " + phrase, 100 + value);
        }
        return numbers;
    }
}
