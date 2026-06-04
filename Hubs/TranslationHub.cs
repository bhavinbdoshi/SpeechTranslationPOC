using Microsoft.AspNetCore.SignalR;
using SpeechTranslationPOC.Models;
using SpeechTranslationPOC.Services;
using System.Collections.Concurrent;

namespace SpeechTranslationPOC.Hubs;

public class TranslationHub : Hub
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SessionUser>> Sessions = new();
    private readonly SpeechTranslationService _translationService;
    private readonly ILogger<TranslationHub> _logger;

    public TranslationHub(
        SpeechTranslationService translationService,
        ILogger<TranslationHub> logger)
    {
        _translationService = translationService;
        _logger = logger;
    }

    public async Task JoinSession(string sessionId, string displayName, string speakLanguage, string listenLanguage)
    {
        var session = Sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, SessionUser>());

        if (session.Count >= 2)
        {
            await Clients.Caller.SendAsync("Error", "Session is full. Only 2 users allowed.");
            return;
        }

        var user = new SessionUser
        {
            ConnectionId = Context.ConnectionId,
            DisplayName = displayName,
            SpeakLanguage = speakLanguage,
            ListenLanguage = listenLanguage,
            ListenOriginal = false
        };

        session[Context.ConnectionId] = user;
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);

        await Clients.Caller.SendAsync("JoinedSession", sessionId, displayName);
        await Clients.OthersInGroup(sessionId).SendAsync("UserJoined", displayName, speakLanguage);

        foreach (var existing in session.Values)
        {
            if (existing.ConnectionId != Context.ConnectionId)
            {
                await Clients.Caller.SendAsync("UserJoined", existing.DisplayName, existing.SpeakLanguage);
            }
        }

        _logger.LogInformation("{User} joined session {Session} (speak={Speak}, listen={Listen})",
            displayName, sessionId, speakLanguage, listenLanguage);
    }

    public async Task SetListenMode(string sessionId, bool listenOriginal)
    {
        if (Sessions.TryGetValue(sessionId, out var session) &&
            session.TryGetValue(Context.ConnectionId, out var user))
        {
            user.ListenOriginal = listenOriginal;
            await Clients.Caller.SendAsync("ListenModeChanged", listenOriginal);
        }
    }

    public async Task SetListenLanguage(string sessionId, string listenLanguage)
    {
        if (Sessions.TryGetValue(sessionId, out var session) &&
            session.TryGetValue(Context.ConnectionId, out var user))
        {
            user.ListenLanguage = listenLanguage;
            await Clients.Caller.SendAsync("ListenLanguageChanged", listenLanguage);
        }
    }

    /// <summary>
    /// Receives a base64-encoded PCM audio chunk from the speaker, decodes it,
    /// translates it for each listener, and sends back the result as base64.
    /// </summary>
    public async Task SendAudio(string sessionId, string pcmAudioBase64)
    {
        try
        {
            if (!Sessions.TryGetValue(sessionId, out var session))
                return;

            if (!session.TryGetValue(Context.ConnectionId, out var speaker))
                return;

            byte[] pcmAudio;
            try
            {
                pcmAudio = Convert.FromBase64String(pcmAudioBase64);
            }
            catch (FormatException)
            {
                _logger.LogWarning("SendAudio: Invalid base64 from {Speaker}.", Context.ConnectionId);
                return;
            }

            if (pcmAudio.Length == 0)
                return;

            _logger.LogDebug("SendAudio: Received {Bytes} bytes from {Speaker} in session {Session}.",
                pcmAudio.Length, speaker.DisplayName, sessionId);

            var listeners = session.Values
                .Where(u => u.ConnectionId != Context.ConnectionId)
                .ToList();

            foreach (var listener in listeners)
            {
                if (listener.ListenOriginal)
                {
                    // Send the original PCM audio as base64
                    await Clients.Client(listener.ConnectionId)
                        .SendAsync("ReceiveAudio", pcmAudioBase64, speaker.DisplayName,
                            string.Empty, string.Empty, true);
                }
                else
                {
                    try
                    {
                        var result = await _translationService.TranslateAudioAsync(
                            pcmAudio, speaker.SpeakLanguage, listener.ListenLanguage, synthesize: true);

                        if (result.Success)
                        {
                            var audioBase64 = result.SynthesizedAudio != null
                                ? Convert.ToBase64String(result.SynthesizedAudio)
                                : string.Empty;

                            await Clients.Client(listener.ConnectionId)
                                .SendAsync("ReceiveAudio",
                                    audioBase64,
                                    speaker.DisplayName,
                                    result.RecognizedText,
                                    result.TranslatedText,
                                    false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Translation failed for {Speaker} -> {Listener}",
                            speaker.DisplayName, listener.DisplayName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendAudio failed for connection {ConnectionId}", Context.ConnectionId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var (sessionId, session) in Sessions)
        {
            if (session.TryRemove(Context.ConnectionId, out var user))
            {
                await Clients.Group(sessionId).SendAsync("UserLeft", user.DisplayName);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);

                if (session.IsEmpty)
                {
                    Sessions.TryRemove(sessionId, out _);
                }

                _logger.LogInformation("{User} left session {Session}", user.DisplayName, sessionId);
                break;
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}