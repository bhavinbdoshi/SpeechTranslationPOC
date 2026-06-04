using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using SpeechTranslationPOC.Models;

namespace SpeechTranslationPOC.Services;

public class AzureSpeechTokenProvider
{
    private readonly AzureSpeechOptions _options;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<AzureSpeechTokenProvider> _logger;

    private AccessToken _cachedToken;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string[] Scopes =
        ["https://cognitiveservices.azure.com/.default"];

    public AzureSpeechTokenProvider(
        IOptions<AzureSpeechOptions> options,
        ILogger<AzureSpeechTokenProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _credential = new DefaultAzureCredential();
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedToken.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _cachedToken.Token;
            }

            _logger.LogInformation("Acquiring new Entra ID token for Azure Speech Service.");

            _cachedToken = await _credential.GetTokenAsync(
                new TokenRequestContext(Scopes), cancellationToken);

            _logger.LogInformation("Token acquired. Expires at {ExpiresOn}.", _cachedToken.ExpiresOn);

            return _cachedToken.Token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire Entra ID token.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}