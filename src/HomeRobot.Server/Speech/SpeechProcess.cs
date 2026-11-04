using System.Diagnostics;
using System.Text;

namespace HomeRobot.Server.Speech;

internal static class SpeechProcess
{
    public static string ResolveExecutable(string path, string root) =>
        path.Contains('/') || path.Contains('\\') ? Path.GetFullPath(path, root) : path;

    public static Process Start(string executable, string root, string provider, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpeechException(503, $"{provider} executable is unavailable.");
        }

        // Drain diagnostics without storing or logging model output.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    public static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
        }
    }
}
