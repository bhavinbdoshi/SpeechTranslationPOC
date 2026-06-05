using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Options;
using SpeechTranslationPOC.Models;

namespace SpeechTranslationPOC.Services;

/// <summary>
/// Factory that creates continuous TranslationSession instances.
/// Supports both standard neural voice translation and Live Interpreter (Personal Voice).
/// </summary>
public class SpeechTranslationService
{
    private readonly AzureSpeechOptions _options;
    private readonly AzureSpeechTokenProvider _tokenProvider;
    private readonly ILogger<SpeechTranslationService> _logger;

    public SpeechTranslationService(
        IOptions<AzureSpeechOptions> options,
        AzureSpeechTokenProvider tokenProvider,
        ILogger<SpeechTranslationService> logger)
    {
        _options = options.Value;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a standard translation session with explicit source language and neural voice.
    /// </summary>
    public async Task<TranslationSession> CreateSessionAsync(
        string sourceLanguage,
        string targetLanguage,
        bool synthesize)
    {
        _logger.LogInformation(
            "[Standard] Creating session: source={Source}, target={Target}, synthesize={Synth}",
            sourceLanguage, targetLanguage, synthesize);

        var authToken = await _tokenProvider.GetTokenAsync();
        _logger.LogInformation("[Standard] Token acquired, length={TokenLength}", authToken.Length);

        var aadToken = $"aad#{_options.ResourceId}#{authToken}";

        var config = SpeechTranslationConfig.FromAuthorizationToken(
            aadToken, _options.Region);

        config.SpeechRecognitionLanguage = sourceLanguage;
        config.AddTargetLanguage(GetLanguageCode(targetLanguage));  // "es-ES" "es"

        if (synthesize)
        {
            var voiceName = GetVoiceName(targetLanguage);
            config.VoiceName = voiceName;
            _logger.LogInformation("[Standard] Voice set to: {Voice}", voiceName);
        }

        var pushStream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

        var audioInput = AudioConfig.FromStreamInput(pushStream);
        var recognizer = new TranslationRecognizer(config, audioInput);

        var session = new TranslationSession(recognizer, pushStream, _logger);
        await session.StartAsync();

        _logger.LogInformation(
            "[Standard] Session started successfully: {Source} -> {Target}",
            sourceLanguage, targetLanguage);

        return session;
    }

    /// <summary>
    /// Creates a Live Interpreter session using the v2 universal endpoint.
    /// Uses auto-detect for source language and Personal Voice for synthesis.
    /// </summary>
    public async Task<TranslationSession> CreatePersonalVoiceSessionAsync(
        string targetLanguage)
    {
        _logger.LogInformation("[PersonalVoice] Creating session: target={Target}, region={Region}",
            targetLanguage, _options.Region);

        var authToken = await _tokenProvider.GetTokenAsync();
        _logger.LogInformation("[PersonalVoice] AAD token acquired, length={Len}", authToken.Length);

        // V2 endpoint -- required for Live Interpreter + Language ID (SDK 1.44+)
        var endpointUrl = $"wss://{_options.Region}.stt.speech.microsoft.com/speech/universal/v2";
        _logger.LogInformation("[PersonalVoice] Endpoint: {Endpoint}", endpointUrl);

        var endpoint = new Uri(endpointUrl);

        // FromEndpoint(Uri) -- no key, use AAD token
        var config = SpeechTranslationConfig.FromEndpoint(endpoint);

        // AAD token MUST be in format: aad#{resourceId}#{token}
        config.AuthorizationToken = $"aad#{_options.ResourceId}#{authToken}";
        _logger.LogInformation("[PersonalVoice] AuthorizationToken set (resourceId={Id})", _options.ResourceId);

        // Target language (just language code, no region)
        config.AddTargetLanguage(GetLanguageCode(targetLanguage));  // "es-ES" -> "es"
        _logger.LogInformation("[PersonalVoice] Target language: {Lang}", GetLanguageCode(targetLanguage));

        // Personal voice name
        config.VoiceName = "personal-voice";
        _logger.LogInformation("[PersonalVoice] VoiceName: personal-voice");

        // Enable SDK file logging to capture handshake/auth details
        var logPath = Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? ".",
            "LogFiles", "speechsdk.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        config.SetProperty(PropertyId.Speech_LogFilename, logPath);
        _logger.LogInformation("[PersonalVoice] SDK log file: {Path}", logPath);

        // No source language -- open range for multilingual/Live Interpreter
        var autoDetectConfig = AutoDetectSourceLanguageConfig.FromOpenRange();
        _logger.LogInformation("[PersonalVoice] AutoDetect OpenRange (no source language set)");

        var pushStream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        var audioInput = AudioConfig.FromStreamInput(pushStream);

        TranslationRecognizer recognizer;
        try
        {
            recognizer = new TranslationRecognizer(config, autoDetectConfig, audioInput);
            _logger.LogInformation("[PersonalVoice] Recognizer created OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersonalVoice] FAILED to create recognizer");
            throw;
        }

        var session = new TranslationSession(recognizer, pushStream, _logger);

        try
        {
            await session.StartAsync();
            _logger.LogInformation("[PersonalVoice] Session started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersonalVoice] FAILED to start recognition");
            await session.DisposeAsync();
            throw;
        }

        return session;
    }

    private static string GetLanguageCode(string locale) => locale.Split('-')[0];

    private static string GetVoiceName(string locale) => locale switch
    {
        "en-US" => "en-US-JennyNeural",
        "es-ES" => "es-ES-ElviraNeural",
        "fr-FR" => "fr-FR-DeniseNeural",
        "de-DE" => "de-DE-KatjaNeural",
        "zh-CN" => "zh-CN-XiaoxiaoNeural",
        "ja-JP" => "ja-JP-NanamiNeural",
        "pt-BR" => "pt-BR-FranciscaNeural",
        "hi-IN" => "hi-IN-SwaraNeural",
        "ar-SA" => "ar-SA-ZariyahNeural",
        "ko-KR" => "ko-KR-SunHiNeural",
        "it-IT" => "it-IT-ElsaNeural",
        "ru-RU" => "ru-RU-SvetlanaNeural",
        _ => "en-US-JennyNeural"
    };
}