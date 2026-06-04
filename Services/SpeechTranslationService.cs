using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Options;
using SpeechTranslationPOC.Models;

namespace SpeechTranslationPOC.Services;

public class SpeechTranslationService : IDisposable
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
    /// Translates a PCM audio chunk (16kHz, 16-bit, mono) from the source language
    /// to the target language. Returns recognized text, translated text, and
    /// optionally synthesized audio bytes of the translation.
    ///
    /// Currently uses the standard Speech Translation API with neural voices.
    /// To enable Live Interpreter with Personal Voice, apply for access at
    /// https://aka.ms/livechatinterpreter, then switch to FromEndpoint() with
    /// the universal v2 endpoint and set VoiceName = "personal-voice".
    /// </summary>
    public async Task<TranslationResult> TranslateAudioAsync(
        byte[] pcmAudio,
        string sourceLanguage,
        string targetLanguage,
        bool synthesize)
    {
        string authToken;
        try
        {
            authToken = await _tokenProvider.GetTokenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire Entra ID token.");
            return new TranslationResult
            {
                Success = false,
                TranslatedText = $"Authentication failed: {ex.Message}"
            };
        }

        // Entra ID tokens must be passed in the format: aad#<resourceId>#<accessToken>
        var aadToken = $"aad#{_options.ResourceId}#{authToken}";

        var config = SpeechTranslationConfig.FromAuthorizationToken(
            aadToken, _options.Region);

        config.SpeechRecognitionLanguage = sourceLanguage;
        config.AddTargetLanguage(GetLanguageCode(targetLanguage));

        if (synthesize)
        {
            config.VoiceName = GetVoiceName(targetLanguage);
        }

        using var pushStream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        pushStream.Write(pcmAudio);
        pushStream.Close();

        using var audioInput = AudioConfig.FromStreamInput(pushStream);
        using var recognizer = new TranslationRecognizer(config, audioInput);

        byte[]? synthesizedAudio = null;

        if (synthesize)
        {
            var tcs = new TaskCompletionSource<byte[]?>();
            recognizer.Synthesizing += (_, e) =>
            {
                var audio = e.Result.GetAudio();
                if (audio.Length > 0)
                {
                    tcs.TrySetResult(audio);
                }
            };

            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
                tcs.TrySetResult(null));

            var result = await recognizer.RecognizeOnceAsync();
            synthesizedAudio = await tcs.Task;

            return BuildResult(result, targetLanguage, synthesizedAudio);
        }
        else
        {
            var result = await recognizer.RecognizeOnceAsync();
            return BuildResult(result, targetLanguage, null);
        }
    }

    private TranslationResult BuildResult(
        TranslationRecognitionResult result,
        string targetLanguage,
        byte[]? synthesizedAudio)
    {
        var langCode = GetLanguageCode(targetLanguage);

        if (result.Reason == ResultReason.TranslatedSpeech)
        {
            result.Translations.TryGetValue(langCode, out var translatedText);

            return new TranslationResult
            {
                RecognizedText = result.Text,
                TranslatedText = translatedText ?? string.Empty,
                SynthesizedAudio = synthesizedAudio,
                Success = true
            };
        }

        if (result.Reason == ResultReason.NoMatch)
        {
            _logger.LogWarning("No speech could be recognized from audio.");
        }
        else if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = CancellationDetails.FromResult(result);
            _logger.LogError("Translation canceled: {Reason} - {ErrorDetails}",
                cancellation.Reason, cancellation.ErrorDetails);
        }

        return new TranslationResult { Success = false };
    }

    private static string GetLanguageCode(string locale) => locale.Split('-')[0];

    /// <summary>
    /// Maps a locale to an Azure neural voice name for speech synthesis.
    /// Used as the standard translation voice. Once Live Interpreter access
    /// is approved, replace with VoiceName = "personal-voice".
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

public class TranslationResult
{
    public string RecognizedText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public byte[]? SynthesizedAudio { get; set; }
    public bool Success { get; set; }
}