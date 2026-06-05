namespace SpeechTranslationPOC.Models;

public class AzureSpeechOptions
{
    public const string SectionName = "AzureSpeech";
    public string Region { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string SpeechAccessKey { get; set; } = string.Empty;
}