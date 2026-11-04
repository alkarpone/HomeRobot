namespace HomeRobot.Server.Speech;

public interface ISpeechToTextService
{
    Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken);
}

public interface ITextToSpeechService
{
    Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

public sealed class SpeechException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class WhisperOptions
{
    public string PythonPath { get; set; } = "python3";
    public string WorkerPath { get; set; } = "";
    public string Language { get; set; } = "cs";
    public string ModelPath { get; set; } = "models/stt/small";
    public string Device { get; set; } = "cpu";
    public string ComputeType { get; set; } = "int8";
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class PiperOptions
{
    public string ExecutablePath { get; set; } = "piper";
    public string VoiceModelPath { get; set; } = "models/tts/cs_CZ-jirka-medium.onnx";
    public int TimeoutSeconds { get; set; } = 60;
}
