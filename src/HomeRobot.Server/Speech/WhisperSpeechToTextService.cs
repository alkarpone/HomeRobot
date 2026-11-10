using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Speech;

public sealed class WhisperSpeechToTextService(
    IOptions<WhisperOptions> configuration, IWebHostEnvironment environment) : ISpeechToTextService, IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private Process? worker;

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken) =>
        (await TranscribeWithConfidenceAsync(audio, cancellationToken)).Text;

    public async Task<SpeechTranscript> TranscribeWithConfidenceAsync(Stream audio, CancellationToken cancellationToken)
    {
        var options = configuration.Value;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var token = deadline.Token;

        try
        {
            using var buffer = new MemoryStream();
            await audio.CopyToAsync(buffer, token);
            var encoded = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (worker is null || worker.HasExited)
                {
                    ResetWorker();
                    worker = StartWorker(options);
                }

                await worker.StandardInput.WriteLineAsync(encoded.AsMemory(), token);
                await worker.StandardInput.FlushAsync(token);
                var line = await worker.StandardOutput.ReadLineAsync(token);
                if (line is null)
                    throw new SpeechException(503, "Whisper worker stopped. Check Python, faster-whisper and the local model.");

                using var result = JsonDocument.Parse(line);
                if (result.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return new(text.GetString()!, result.RootElement.TryGetProperty("clearSpeech", out var clear) &&
                        clear.ValueKind == JsonValueKind.True);
                if (result.RootElement.TryGetProperty("error", out var error) && error.GetString() == "initialization")
                    throw new SpeechException(503, "Whisper could not load faster-whisper or its local model.");

                throw new SpeechException(502, "Whisper transcription failed.");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                ResetWorker();
                throw new SpeechException(502, "Whisper worker communication failed.");
            }
            catch
            {
                ResetWorker();
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechException(504, "Whisper transcription timed out.");
        }
    }

    private Process StartWorker(WhisperOptions options)
    {
        var root = environment.ContentRootPath;
        var model = Path.GetFullPath(options.ModelPath, root);
        var script = string.IsNullOrEmpty(options.WorkerPath)
            ? Path.Combine(AppContext.BaseDirectory, "Speech", "whisper_worker.py")
            : Path.GetFullPath(options.WorkerPath, root);
        if (!File.Exists(script) || !File.Exists(Path.Combine(model, "model.bin")))
            throw new SpeechException(503, "Whisper worker or local model is missing.");

        return SpeechProcess.Start(SpeechProcess.ResolveExecutable(options.PythonPath, root), root, "Whisper",
            "-u", script, model, options.Language, options.Device, options.ComputeType, options.InitialPrompt);
    }

    private void ResetWorker()
    {
        if (worker is null)
            return;
        SpeechProcess.Stop(worker);
        worker = null;
    }

    public void Dispose()
    {
        ResetWorker();
        gate.Dispose();
    }
}
