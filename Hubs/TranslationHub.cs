using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SpeechTranslationPOC.Models;
using SpeechTranslationPOC.Services;

namespace SpeechTranslationPOC.Hubs;

public class TranslationHub : Hub
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SessionUser>> Sessions = new();
    private static readonly ConcurrentDictionary<string, TranslationSession> TranslationSessions = new();

    private readonly SpeechTranslationService _translationService;
    private readonly IHubContext<TranslationHub> _hubContext;
    private readonly ILogger<TranslationHub> _logger;

    public TranslationHub(
        SpeechTranslationService translationService,
        IHubContext<TranslationHub> hubContext,
        ILogger<TranslationHub> logger)
    {
        _translationService = translationService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task JoinSession(string sessionId, string displayName, string speakLanguage,
        string listenLanguage, bool usePersonalVoice)
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
            ListenOriginal = false,
            UsePersonalVoice = usePersonalVoice
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

        _logger.LogInformation(
            "{User} joined session {Session} (speak={Speak}, listen={Listen}, personalVoice={PV})",
            displayName, sessionId, speakLanguage, listenLanguage, usePersonalVoice);

        // Auto-start translation when both users are present
        if (session.Count == 2)
        {
            await StartTranslationForSession(sessionId, session);
        }
    }

    private async Task StartTranslationForSession(
        string sessionId,
        ConcurrentDictionary<string, SessionUser> session)
    {
        var users = session.Values.ToList();
        if (users.Count != 2) return;

        var userA = users[0];
        var userB = users[1];

        await CreateTranslationLink(sessionId, userA, userB);
        await CreateTranslationLink(sessionId, userB, userA);

        _logger.LogInformation("Auto-started translation for session {Session}", sessionId);
    }

    private async Task CreateTranslationLink(string sessionId, SessionUser speaker, SessionUser listener)
    {
        if (listener.ListenOriginal) return;

        var sessionKey = GetSessionKey(sessionId, speaker.ConnectionId);

        if (TranslationSessions.TryRemove(sessionKey, out var old))
        {
            await old.DisposeAsync();
        }

        try
        {
            TranslationSession translationSession;

            if (speaker.UsePersonalVoice)
            {
                // Live Interpreter: auto-detect source, personal voice output
                translationSession = await _translationService.CreatePersonalVoiceSessionAsync(
                    listener.ListenLanguage);
            }
            else
            {
                // Standard: explicit source language, neural voice
                translationSession = await _translationService.CreateSessionAsync(
                    speaker.SpeakLanguage, listener.ListenLanguage, synthesize: true);
            }

            var hubContext = _hubContext;
            var speakerName = speaker.DisplayName;
            var listenerConnId = listener.ConnectionId;

            translationSession.OnPartialResult += async (recognizedText, partialTranslation) =>
            {
                await hubContext.Clients.Client(listenerConnId)
                    .SendAsync("ReceivePartial", speakerName, recognizedText, partialTranslation);
            };

            translationSession.OnTranslationTextReceived += async (result) =>
            {
                await hubContext.Clients.Client(listenerConnId)
                    .SendAsync("ReceiveText", speakerName, result.RecognizedText, result.TranslatedText);
            };

            translationSession.OnSynthesisAudioReceived += async (audioBytes) =>
            {
                var audioBase64 = Convert.ToBase64String(audioBytes);
                await hubContext.Clients.Client(listenerConnId)
                    .SendAsync("ReceiveAudio", audioBase64, speakerName);
            };

            TranslationSessions[sessionKey] = translationSession;

            _logger.LogInformation(
                "Translation link created: {Speaker} -> {Listener} (personalVoice={PV})",
                speakerName, listener.DisplayName, speaker.UsePersonalVoice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create translation link {Speaker} -> {Listener}",
                speaker.DisplayName, listener.DisplayName);
            
            var errorMsg = ex.Message.Contains("Live Interpreter")
                ? "Personal Voice (Live Interpreter) access not yet approved for this resource. Please use Standard mode."
                : "Failed to start translation: " + ex.Message;
            
            await _hubContext.Clients.Client(speaker.ConnectionId)
                .SendAsync("Error", errorMsg);
        }
    }

    public async Task SetListenMode(string sessionId, bool listenOriginal)
    {
        if (!Sessions.TryGetValue(sessionId, out var session))
            return;
        if (!session.TryGetValue(Context.ConnectionId, out var user))
            return;

        user.ListenOriginal = listenOriginal;
        await Clients.Caller.SendAsync("ListenModeChanged", listenOriginal);

        var speaker = session.Values.FirstOrDefault(u => u.ConnectionId != Context.ConnectionId);
        if (speaker == null) return;

        var sessionKey = GetSessionKey(sessionId, speaker.ConnectionId);

        if (listenOriginal)
        {
            if (TranslationSessions.TryRemove(sessionKey, out var ts))
            {
                await ts.DisposeAsync();
            }
        }
        else
        {
            await CreateTranslationLink(sessionId, speaker, user);
        }
    }

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
                return;
            }

            if (pcmAudio.Length == 0)
                return;

            var originalListeners = session.Values
                .Where(u => u.ConnectionId != Context.ConnectionId && u.ListenOriginal)
                .ToList();

            foreach (var listener in originalListeners)
            {
                await Clients.Client(listener.ConnectionId)
                    .SendAsync("ReceiveOriginalAudio", pcmAudioBase64, speaker.DisplayName);
            }

            var sessionKey = GetSessionKey(sessionId, Context.ConnectionId);
            if (TranslationSessions.TryGetValue(sessionKey, out var translationSession))
            {
                translationSession.WriteAudio(pcmAudio);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendAudio failed for {ConnectionId}.", Context.ConnectionId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var speakerKeys = TranslationSessions.Keys
            .Where(k => k.EndsWith("_" + Context.ConnectionId))
            .ToList();

        foreach (var key in speakerKeys)
        {
            if (TranslationSessions.TryRemove(key, out var ts))
            {
                await ts.DisposeAsync();
            }
        }

        foreach (var (sessionId, session) in Sessions)
        {
            if (session.ContainsKey(Context.ConnectionId))
            {
                var otherUser = session.Values.FirstOrDefault(u => u.ConnectionId != Context.ConnectionId);
                if (otherUser != null)
                {
                    var otherKey = GetSessionKey(sessionId, otherUser.ConnectionId);
                    if (TranslationSessions.TryRemove(otherKey, out var otherTs))
                    {
                        await otherTs.DisposeAsync();
                    }
                }
            }
        }

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

    private static string GetSessionKey(string sessionId, string connectionId)
        => sessionId + "_" + connectionId;
}