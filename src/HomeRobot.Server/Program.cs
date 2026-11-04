using HomeRobot.Server.Speech;
using HomeRobot.Server.AI;
using Microsoft.Extensions.Options;
using HomeRobot.Server.Communication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<RobotWebSocket>();
builder.Services.AddSingleton<RobotRegistry>();
builder.Services.AddTransient<IRobotCommandInterpreter, RobotCommandInterpreter>();
builder.Services.AddSingleton<RobotPlanExecutor>();
builder.Services.AddHostedService(services => services.GetRequiredService<RobotPlanExecutor>());
builder.Services.AddOptions<RobotProtocolOptions>().BindConfiguration("RobotProtocol")
    .Validate(options => options.IsValid(), "Invalid robot protocol configuration.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddOptions<OllamaOptions>()
    .BindConfiguration("Ollama")
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
                         uri.Scheme is "http" or "https", "Ollama requires an HTTP(S) base URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Model) && options.TimeoutSeconds > 0,
        "Ollama requires a model and a positive timeout.")
    .ValidateOnStart();
builder.Services.AddHttpClient<OllamaProvider>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.MaxResponseContentBufferSize = 256 * 1024;
});
builder.Services.AddTransient<IAIProvider>(services => services.GetRequiredService<OllamaProvider>());

builder.Services.AddOptions<WhisperOptions>().BindConfiguration("Speech:STT")
    .Validate(options => options.TimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(options.ModelPath) &&
                         !string.IsNullOrWhiteSpace(options.PythonPath), "Invalid Whisper configuration.")
    .ValidateOnStart();
builder.Services.AddOptions<PiperOptions>().BindConfiguration("Speech:TTS")
    .Validate(options => options.TimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(options.VoiceModelPath) &&
                         !string.IsNullOrWhiteSpace(options.ExecutablePath), "Invalid Piper configuration.")
    .ValidateOnStart();
builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
builder.Services.AddSingleton<ITextToSpeechService, PiperTextToSpeechService>();

var app = builder.Build();
app.UseWebSockets();
app.MapControllers();
app.MapGet("/ws/robot", (HttpContext context, RobotWebSocket robot) => robot.HandleAsync(context));
app.Run();

public partial class Program { }
