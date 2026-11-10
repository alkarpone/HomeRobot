using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Speech;
using Xunit;

namespace HomeRobot.Tests;

public sealed class VoiceIntentTests
{
    [Theory]
    [InlineData("Otoď se doprava o 90 stupnů rychlostí 30%.", "rotate_right", 90, 30)]
    [InlineData("Otoč se do leva o devadesát stupňů na 30 %", "rotate_left", 90, 30)]
    [InlineData("Mohl by ses otočit doprava o půl otáčky rychlostí 20 procent?", "rotate_right", 180, 20)]
    [InlineData("Prosím tě, popojeď půl metru dopředu na 25 %.", "move_forward", 50, 25)]
    [InlineData("Popojet padesát centimetrů dopředu na 35 %", "move_forward", 50, 35)]
    [InlineData("Couvni deset centimetrů rychlostí 20 procent", "move_backward", 10, 20)]
    [InlineData("Couvni o 7 milimetrů na 30 %", "move_backward", 0.7, 30)]
    [InlineData("Můžeš popojet vpřed o 80 cm při rychlosti 35 procent?", "move_forward", 80, 35)]
    [InlineData("Mohl bys prosím zacouvat o půl metru tempem 20 %?", "move_backward", 50, 20)]
    [InlineData("Otoč se o 45 stupňů vlevo, dvacet procentní rychlostí.", "rotate_left", 45, 20)]
    [InlineData("Robote, můžeš se otočit o 90° vpravo na 25 %?", "rotate_right", 90, 25)]
    public void PreservesSpokenDirectionQuantityAndSpeed(string text, string command, double amount, double speed)
    {
        var result = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal("action", result.Route);
        var action = Assert.Single(result.Actions!);
        Assert.Equal(command, action.Command);
        if (command.StartsWith("rotate", StringComparison.Ordinal))
            Assert.Equal(new RotateParameters(amount, speed), action.Parameters);
        else
            Assert.Equal(new MoveParameters(amount, speed), action.Parameters);
    }

    [Theory]
    [InlineData("Můžeš se usmát?", "happy")]
    [InlineData("Mohl by ses zamračit?", "sad")]
    public void RecognizesPoliteExpressionRequests(string text, string expression)
    {
        var result = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal("action", result.Route);
        Assert.Equal(new RobotCommand("set_expression", new ExpressionParameters(expression)), Assert.Single(result.Actions!));
    }

    [Fact]
    public void RequiresEveryClauseToBeComplete()
    {
        const string first = "Jeď dozadu dvacet centimetrů a pak se otoč doprava";
        var complete = VoiceIntent.Analyze(new SpeechTranscript(first + " o 90° na 30 %", true), 20);
        Assert.Equal(new[] { "move_backward", "rotate_right" }, complete.Actions?.Select(action => action.Command));
        Assert.Equal(new MoveParameters(20, 20), complete.Actions![0].Parameters);
        Assert.Equal(new RotateParameters(90, 30), complete.Actions[1].Parameters);
        var incomplete = VoiceIntent.Analyze(new SpeechTranscript(first, true), 30);
        Assert.Equal("clarification_required", incomplete.Route);
        Assert.Null(incomplete.Actions);
    }

    [Theory]
    [InlineData("Jeď rovně 10 centimetrů odboč do prava 45 stupňů.")]
    [InlineData("Popojeď o 10 cm vpřed, zatoč doprava o 45°.")]
    [InlineData("Jeď dopředu deset centimetrů. Otoč se vpravo o 45 stupňů.")]
    public void RecognizesConsecutiveMovementCommandsWithoutConjunction(string text)
    {
        var result = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal("action", result.Route);
        Assert.Equal<RobotCommand>(
            [new("move_forward", new MoveParameters(10, 30)), new("rotate_right", new RotateParameters(45, 30))],
            result.Actions!);
    }

    [Theory]
    [InlineData("Jeď rovně 10 centimetrů odboč doprava.")]
    [InlineData("Jeď rovně 10 centimetrů neodbočuj doprava o 45 stupňů.")]
    [InlineData("Nejeď rovně 10 centimetrů odboč doprava o 45 stupňů.")]
    public void RejectsIncompleteOrNegatedCommandsWithoutConjunction(string text)
    {
        var result = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Null(result.Actions);
    }

    [Theory]
    [InlineData("Odbols do prava", "Odboč doprava", "rotate_right", "Missing rotation angle.")]
    [InlineData("Popojet padesát centimetrů", "Popojeď padesát centimetrů", "movement_unspecified", "Specify a direction.")]
    public void CorrectsKnownErrorsWithoutSupplyingMissingParameters(string text, string corrected, string intent, string reason)
    {
        var result = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal(corrected, result.Text);
        Assert.Equal(intent, result.Intent);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Actions);
    }
}
