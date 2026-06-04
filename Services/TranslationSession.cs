using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;

namespace SpeechTranslationPOC.Services;

/// <summary>
/// A persistent continuous translation session. Push PCM audio via WriteAudio();
/// text results and audio results are delivered independently via events.
/// </summary>
public sealed class TranslationSession : IAsyncDisposable
{
    private readonly TranslationRecognizer _recognizer;
    private readonly PushAudioInputStream _pushStream;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Fires when a complete sentence is recognized and translated (text only).</summary>
    public event Func<TranslationTextResult, Task>? OnTranslationTextReceived;

    /// <summary>Fires with partial/interim translation text while the speaker is still talking.</summary>
    public event Func<string, string, Task>? OnPartialResult;

    /// <summary>Fires when synthesized audio is available (may arrive after text).</summary>
    public event Func<byte[], Task>? OnSynthesisAudioReceived;

    public TranslationSession(
        TranslationRecognizer recognizer,
        PushAudioInputStream pushStream,
        ILogger logger)
    {
        _recognizer = recognizer;
        _pushStream = pushStream;
        _logger = logger;

        _recognizer.Recognizing += HandleRecognizing;
        _recognizer.Recognized += HandleRecognized;
        _recognizer.Synthesizing += HandleSynthesizing;
        _recognizer.Canceled += HandleCanceled;
    }

    private void HandleRecognizing(object? sender, TranslationRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.TranslatingSpeech)
        {
            var partialTranslation = string.Empty;
            foreach (var kvp in e.Result.Translations)
            {
                partialTranslation = kvp.Value;
                break;
            }

            try
            {
                OnPartialResult?.Invoke(e.Result.Text, partialTranslation);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in OnPartialResult callback.");
            }
        }
    }

    private void HandleRecognized(object? sender, TranslationRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.TranslatedSpeech)
        {
            var translatedText = string.Empty;
            foreach (var kvp in e.Result.Translations)
            {
                translatedText = kvp.Value;
                break;
            }

            var result = new TranslationTextResult
            {
                RecognizedText = e.Result.Text,
                TranslatedText = translatedText
            };

            try
            {
                OnTranslationTextReceived?.Invoke(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in OnTranslationTextReceived callback.");
            }
        }
        else if (e.Result.Reason == ResultReason.NoMatch)
        {
            _logger.LogDebug("Continuous recognition: no match in audio segment.");
        }
    }

    private void HandleSynthesizing(object? sender, TranslationSynthesisEventArgs e)
    {
        var audio = e.Result.GetAudio();
        if (audio.Length > 0)
        {
            try
            {
                OnSynthesisAudioReceived?.Invoke(audio);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in OnSynthesisAudioReceived callback.");
            }
        }
    }

    private void HandleCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        if (e.Reason == CancellationReason.Error)
        {
            _logger.LogError(
                "Continuous recognition canceled: ErrorCode={ErrorCode}, Details={Details}",
                e.ErrorCode, e.ErrorDetails);
        }
        else
        {
            _logger.LogInformation("Continuous recognition ended: {Reason}", e.Reason);
        }
    }

    public async Task StartAsync()
    {
        await _recognizer.StartContinuousRecognitionAsync();
        _logger.LogInformation("Continuous recognition started.");
    }

    /// <summary>Pushes raw PCM audio (16kHz, 16-bit, mono) into the recognizer.</summary>
    public void WriteAudio(byte[] pcmAudio)
    {
        if (!_disposed)
        {
            _pushStream.Write(pcmAudio);
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        _pushStream.Close();
        await _recognizer.StopContinuousRecognitionAsync();
        _logger.LogInformation("Continuous recognition stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _recognizer.Recognizing -= HandleRecognizing;
        _recognizer.Recognized -= HandleRecognized;
        _recognizer.Synthesizing -= HandleSynthesizing;
        _recognizer.Canceled -= HandleCanceled;

        try
        {
            _pushStream.Close();
            await _recognizer.StopContinuousRecognitionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during TranslationSession dispose.");
        }

        _recognizer.Dispose();
        _pushStream.Dispose();
    }
}

/// <summary>Text-only translation result (audio delivered separately).</summary>
public class TranslationTextResult
{
    public string RecognizedText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
}