using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Options;
using SpeechTranslationPOC.Models;

namespace SpeechTranslationPOC.Services;

/// <summary>
/// Factory that creates continuous TranslationSession instances.
/// Each session holds a persistent recognizer and push stream.
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
    /// Creates and starts a continuous translation session.
    ///
    /// Currently uses the standard Speech Translation API with neural voices.
    /// To enable Live Interpreter with Personal Voice and auto-detect, apply
    /// for access at https://aka.ms/livechatinterpreter, then switch to
    /// FromEndpoint() with the universal v2 endpoint.
    /// </summary>
    public async Task<TranslationSession> CreateSessionAsync(
        string sourceLanguage,
        string targetLanguage,
        bool synthesize)
    {
        var authToken = await _tokenProvider.GetTokenAsync();

        // Entra ID tokens use format: aad#<resourceId>#<accessToken>
        var aadToken = $"aad#{_options.ResourceId}#{authToken}";

        var config = SpeechTranslationConfig.FromAuthorizationToken(
            aadToken, _options.Region);

        config.SpeechRecognitionLanguage = sourceLanguage;
        config.AddTargetLanguage(GetLanguageCode(targetLanguage));

        if (synthesize)
        {
            config.VoiceName = GetVoiceName(targetLanguage);
        }

        var pushStream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

        var audioInput = AudioConfig.FromStreamInput(pushStream);
        var recognizer = new TranslationRecognizer(config, audioInput);

        var session = new TranslationSession(recognizer, pushStream, _logger);
        await session.StartAsync();

        _logger.LogInformation(
            "Created translation session: {Source} -> {Target} (synthesize={Synth})",
            sourceLanguage, targetLanguage, synthesize);

        return session;
    }

    private static string GetLanguageCode(string locale) => locale.Split('-')[0];

    /// <summary>
    /// Maps a locale to an Azure neural voice name for speech synthesis.
    /// Once Live Interpreter access is approved, replace with "PersonalVoiceNeural".
    /// </summary>
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