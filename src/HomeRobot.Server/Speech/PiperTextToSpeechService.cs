using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Speech;

public sealed class PiperTextToSpeechService(
    IOptions<PiperOptions> configuration, IWebHostEnvironment environment) : ITextToSpeechService
{
    private readonly SemaphoreSlim gate = new(1);

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var options = configuration.Value;
        var root = environment.ContentRootPath;
        var model = Path.GetFullPath(options.VoiceModelPath, root);
        if (!File.Exists(model) || !File.Exists(model + ".json"))
            throw new SpeechException(503, "Piper voice model or voice configuration is missing.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var token = deadline.Token;
        try
        {
            await gate.WaitAsync(token);
            try
            {
                return await RunAsync(text, model, options, root, token);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechException(504, "Piper synthesis timed out.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException(502, "Piper could not produce WAV audio.");
        }
    }

    private static async Task<byte[]> RunAsync(
        string text, string model, PiperOptions options, string root, CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("homerobot-piper-");
        try
        {
            var input = Path.Combine(directory.FullName, "input.txt");
            var output = Path.Combine(directory.FullName, "output.wav");
            await File.WriteAllTextAsync(input, text, token);
            var process = SpeechProcess.Start(SpeechProcess.ResolveExecutable(options.ExecutablePath, root),
                root, "Piper", "--model", model, "--input-file", input, "--output-file", output);
            try
            {
                process.StandardInput.Close();
                process.OutputDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0 || !File.Exists(output))
                    throw new SpeechException(502, "Piper synthesis failed.");

                var wav = await File.ReadAllBytesAsync(output, token);
                if (wav.Length <= 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                    !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                    throw new SpeechException(502, "Piper returned invalid WAV audio.");
                return wav;
            }
            finally
            {
                SpeechProcess.Stop(process);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
