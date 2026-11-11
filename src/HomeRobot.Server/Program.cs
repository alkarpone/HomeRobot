using HomeRobot.Server.Speech;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Vision;
using HomeRobot.Server.Memory;
using HomeRobot.Server.AI;
using Microsoft.Extensions.Options;
using HomeRobot.Server.Communication;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5000");

var settings = new DashboardSettings(builder.Configuration["Dashboard:SettingsPath"] ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeRobot", "dashboard-settings.json"));
settings.AddStartupOverrides(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<DashboardAccess>();
builder.Services.AddSingleton<DashboardDiagnostics>();
builder.Services.AddSingleton<DashboardLogs>();
builder.Services.AddSingleton<ILoggerProvider>(services => services.GetRequiredService<DashboardLogs>());
builder.Services.AddControllers();
builder.Services.AddSingleton<RobotWebSocket>();
builder.Services.AddSingleton<RobotRegistry>();
builder.Services.AddSingleton<AudioPlaybackService>();
builder.Services.AddHostedService(services => services.GetRequiredService<AudioPlaybackService>());
builder.Services.AddTransient<IRobotCommandInterpreter, RobotCommandInterpreter>();
builder.Services.AddSingleton<RobotPlanExecutor>();
builder.Services.AddHostedService(services => services.GetRequiredService<RobotPlanExecutor>());
builder.Services.AddOptions<RobotProtocolOptions>().BindConfiguration("RobotProtocol")
    .Validate(options => options.IsValid(), "Invalid robot protocol configuration.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VisionContext>();
builder.Services.AddTransient<VisionService>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<ConversationPrompt>();
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddSingleton<MemoryCommands>();
builder.Services.AddSingleton<IMemoryRetrieval, MemoryRetrieval>();
builder.Services.AddOptions<MemoryOptions>().BindConfiguration("Memory")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabasePath), "Memory requires a database path.")
    .ValidateOnStart();
builder.Services.AddOptions<PersonalityProfile>().BindConfiguration("Personality")
    .Validate(profile => profile.IsValid(), "Invalid personality profile.").ValidateOnStart();
builder.Services.AddOptions<ConversationOptions>().BindConfiguration("Conversation")
    .Validate(options => options.MaxHistoryMessages is >= 0 and <= 40 && options.MaxHistoryMessages % 2 == 0,
        "MaxHistoryMessages must be an even number between 0 and 40.").ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>()
    .BindConfiguration("Ollama")
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
                         uri.Scheme is "http" or "https", "Ollama requires an HTTP(S) base URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Model) && options.TimeoutSeconds > 0,
        "Ollama requires a model and a positive timeout.")
    .Validate(options => options.Temperature is null or >= 0 and <= 2 &&
                         options.MaxResponseTokens is null or >= 1 and <= 8192, "Invalid Ollama generation options.")
    .ValidateOnStart();
builder.Services.AddHttpClient<OllamaProvider>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.MaxResponseContentBufferSize = 256 * 1024;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IAIProvider>(services => services.GetRequiredService<OllamaProvider>());

var serverRoot = builder.Environment.ContentRootPath;
var repositoryRoot = Path.GetFullPath("../..", serverRoot);
var venvBin = Path.Combine(repositoryRoot, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin");

string? LocalSpeechExecutable(string name)
{
    var executable = Path.Combine(venvBin, OperatingSystem.IsWindows() ? name + ".exe" : name);
    return File.Exists(Path.Combine(repositoryRoot, "HomeRobot.slnx")) && File.Exists(executable)
        ? executable : null;
}

builder.Services.AddOptions<WhisperOptions>().BindConfiguration("Speech:STT")
    .PostConfigure(options =>
    {
        if (builder.Configuration["Speech:STT:PythonPath"] is null)
            options.PythonPath = LocalSpeechExecutable("python") ?? options.PythonPath;

        const string mediumModel = "models/stt/medium";
        if (builder.Configuration["Speech:STT:ModelPath"] is null &&
            !File.Exists(Path.Combine(serverRoot, options.ModelPath, "model.bin")) &&
            File.Exists(Path.Combine(serverRoot, mediumModel, "model.bin")))
            options.ModelPath = mediumModel;
    })
    .Validate(options => options.TimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(options.ModelPath) &&
                         !string.IsNullOrWhiteSpace(options.PythonPath), "Invalid Whisper configuration.")
    .ValidateOnStart();
builder.Services.AddOptions<PiperOptions>().BindConfiguration("Speech:TTS")
    .PostConfigure(options =>
    {
        if (builder.Configuration["Speech:TTS:ExecutablePath"] is null)
            options.ExecutablePath = LocalSpeechExecutable("piper") ?? options.ExecutablePath;
    })
    .Validate(options => options.TimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(options.VoiceModelPath) &&
                         !string.IsNullOrWhiteSpace(options.ExecutablePath), "Invalid Piper configuration.")
    .ValidateOnStart();
builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
builder.Services.AddSingleton<ITextToSpeechService, PiperTextToSpeechService>();
builder.Services.AddHttpClient<Esp32SpeakerClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

var app = builder.Build();
app.UseExceptionHandler(error => error.Run(context =>
    Results.Problem(statusCode: 500, title: "An internal server error occurred.").ExecuteAsync(context)));
_ = app.Services.GetRequiredService<DashboardDiagnostics>();
var access = app.Services.GetRequiredService<DashboardAccess>();
app.Use((context, next) => access.InvokeAsync(context, next));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.MapControllers();
app.MapGet("/ws/robot", (HttpContext context, RobotWebSocket robot) => robot.HandleAsync(context));
app.Run();

public partial class Program { }
