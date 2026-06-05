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
    private int _audioChunksReceived;
    private int _recognizedCount;
    private int _synthesisCount;

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
        _recognizer.SessionStarted += HandleSessionStarted;
        _recognizer.SessionStopped += HandleSessionStopped;
    }

    private void HandleSessionStarted(object? sender, SessionEventArgs e)
    {
        _logger.LogInformation("[Session] SDK session started. SessionId={SessionId}", e.SessionId);
    }

    private void HandleSessionStopped(object? sender, SessionEventArgs e)
    {
        _logger.LogInformation("[Session] SDK session stopped. SessionId={SessionId}", e.SessionId);
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

            _logger.LogDebug("[Session] Partial: \"{Text}\" -> \"{Translation}\"",
                e.Result.Text, partialTranslation);

            try
            {
                OnPartialResult?.Invoke(e.Result.Text, partialTranslation);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Session] Error in OnPartialResult callback.");
            }
        }
    }

    private void HandleRecognized(object? sender, TranslationRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.TranslatedSpeech)
        {
            _recognizedCount++;
            var translatedText = string.Empty;
            foreach (var kvp in e.Result.Translations)
            {
                translatedText = kvp.Value;
                break;
            }

            Console.WriteLine($"[Session] RECOGNIZED #{_recognizedCount}: \"{e.Result.Text}\" -> \"{translatedText}\"");
            _logger.LogInformation("[Session] Recognized #{Count}: \"{Text}\" -> \"{Translation}\"",
                _recognizedCount, e.Result.Text, translatedText);

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
                _logger.LogWarning(ex, "[Session] Error in OnTranslationTextReceived callback.");
            }
        }
        else if (e.Result.Reason == ResultReason.NoMatch)
        {
            Console.WriteLine("[Session] NoMatch - no speech recognized");
            _logger.LogInformation("[Session] NoMatch");
        }
        else
        {
            Console.WriteLine($"[Session] Recognized with unexpected reason: {e.Result.Reason}");
        }
    }

    private void HandleSynthesizing(object? sender, TranslationSynthesisEventArgs e)
    {
        var audio = e.Result.GetAudio();

        Console.WriteLine($"[Session] SYNTHESIZING: {audio.Length} bytes, Reason={e.Result.Reason}");
        _logger.LogInformation("[Session] Synthesizing: {Bytes} bytes, Reason={Reason}",
            audio.Length, e.Result.Reason);

        if (audio.Length > 0)
        {
            _synthesisCount++;
            Console.WriteLine($"[Session] Synthesis #{_synthesisCount}: sending {audio.Length} bytes");

            try
            {
                OnSynthesisAudioReceived?.Invoke(audio);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Session] ERROR in OnSynthesisAudioReceived: {ex.Message}");
                _logger.LogWarning(ex, "[Session] Error in OnSynthesisAudioReceived callback.");
            }
        }
        else
        {
            Console.WriteLine("[Session] WARNING: Synthesizing fired with 0 bytes");
        }
    }

    private void HandleCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        Console.WriteLine($"[Session] CANCELED: Reason={e.Reason}, ErrorCode={e.ErrorCode}, Details={e.ErrorDetails}");
        _logger.LogError("[Session] CANCELED: Reason={Reason}, ErrorCode={Code}, Details={Details}",
            e.Reason, e.ErrorCode, e.ErrorDetails);
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("[Session] Starting continuous recognition...");
        await _recognizer.StartContinuousRecognitionAsync();
        _logger.LogInformation("[Session] Continuous recognition started successfully.");
    }

    /// <summary>Pushes raw PCM audio (16kHz, 16-bit, mono) into the recognizer.</summary>
    public void WriteAudio(byte[] pcmAudio)
    {
        if (!_disposed)
        {
            _audioChunksReceived++;
            _pushStream.Write(pcmAudio);

            // Log every 50 chunks (~10 seconds of audio) to avoid flooding
            if (_audioChunksReceived % 50 == 0)
            {
                _logger.LogInformation(
                    "[Session] Audio progress: {Chunks} chunks received ({TotalBytes} bytes total)",
                    _audioChunksReceived, (long)_audioChunksReceived * pcmAudio.Length);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        _logger.LogInformation(
            "[Session] Stopping. Stats: {Chunks} audio chunks, {Recognized} recognitions, {Synthesis} synthesis events",
            _audioChunksReceived, _recognizedCount, _synthesisCount);
        _pushStream.Close();
        await _recognizer.StopContinuousRecognitionAsync();
        _logger.LogInformation("[Session] Stopped successfully.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("[Session] Disposing...");

        _recognizer.Recognizing -= HandleRecognizing;
        _recognizer.Recognized -= HandleRecognized;
        _recognizer.Synthesizing -= HandleSynthesizing;
        _recognizer.Canceled -= HandleCanceled;
        _recognizer.SessionStarted -= HandleSessionStarted;
        _recognizer.SessionStopped -= HandleSessionStopped;

        try
        {
            _pushStream.Close();
            await _recognizer.StopContinuousRecognitionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Session] Error during dispose.");
        }

        _recognizer.Dispose();
        _pushStream.Dispose();
        _logger.LogInformation("[Session] Disposed.");
    }
}

/// <summary>Text-only translation result (audio delivered separately).</summary>
public class TranslationTextResult
{
    public string RecognizedText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
}