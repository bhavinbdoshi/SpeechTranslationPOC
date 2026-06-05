namespace SpeechTranslationPOC.Models;

public class SessionUser
{
    public string ConnectionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SpeakLanguage { get; set; } = "en-US";
    public string ListenLanguage { get; set; } = "en-US";
    public bool ListenOriginal { get; set; } = false;
    public bool UsePersonalVoice { get; set; } = false;
}