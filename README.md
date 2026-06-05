# Azure Speech Translation POC

> **DISCLAIMER: This project is for testing, demonstration, and experimental purposes only. It is NOT production-ready code. Do not use this in production environments. There is no warranty, SLA, or support provided. Use at your own risk.**

## Overview

A real-time speech-to-speech translation proof of concept built with ASP.NET Core Razor Pages and Azure Cognitive Services Speech SDK. Two users join a session, speak in their own language, and hear each other's speech translated in near real-time.

### Features

- **Real-time speech translation** using Azure Speech Translation API with continuous recognition
- **Live Interpreter (Personal Voice)** support using the v2 universal endpoint (requires separate access approval)
- **Auto-detect source language** when using Live Interpreter mode
- **Neural voice synthesis** for standard translation mode
- **Partial (interim) results** displayed in real-time while the speaker is still talking
- **SignalR WebSocket** communication for low-latency audio streaming between browser and server
- **AudioWorklet-based** microphone capture at 16kHz with 200ms chunking

## Architecture

Browser->>Hub: JoinSession(sessionId, name, speakLang, listenLang)
Note over Hub: When 2 users join, auto-starts translation

Hub->>Service: CreateSessionAsync(sourceLang, targetLang, synthesize)
Service->>SDK: Create TranslationRecognizer + PushAudioInputStream
SDK->>Azure: Opens persistent WebSocket (wss://)
SDK-->>Service: Recognizer ready
Service-->>Hub: TranslationSession returned

loop Continuous audio streaming
    Browser->>Hub: SendAudio(sessionId, pcmBase64)
    Hub->>SDK: WriteAudio(pcmBytes) via PushStream
    SDK->>Azure: Audio packets (16kHz/16-bit/mono PCM)

    Azure-->>SDK: Recognizing event (partial results)
    SDK-->>Hub: OnPartialResult(text, partialTranslation)
    Hub-->>Browser: ReceivePartial (live captions)

    Azure-->>SDK: Recognized event (final translation)
    SDK-->>Hub: OnTranslationTextReceived(result)

    Azure-->>SDK: Synthesizing event (audio chunks)
    SDK-->>Hub: OnSynthesisAudioReceived(audioBytes)
    Hub-->>Browser: ReceiveAudio (translated speech)
end

Browser->>Hub: OnDisconnectedAsync
Hub->>SDK: StopContinuousRecognitionAsync + Dispose
SDK->>Azure: Close WebSocket

## Translation Modes

### Standard (Neural Voice)

- User selects source and target language
- Translation uses Azure Neural Voice for synthesis (e.g., `en-US-JennyNeural`)
- Uses `SpeechTranslationConfig.FromAuthorizationToken` with Entra ID (AAD)

### Live Interpreter (Personal Voice)

- Auto-detects source language (no selection needed)
- Translation uses the speaker's own voice clone
- Uses `SpeechTranslationConfig.FromEndpoint` with the v2 universal endpoint
- **Requires separate access approval** at https://aka.ms/customneural
- Voice name: `personal-voice`

## Concept

###How Azure Speech Translation works

Speech translation chains three capabilities together in real time over a single streaming connection:

Speech-to-Text — recognizes the spoken source language
Translation — translates the recognized text into one or more target languages
Text-to-Speech (optional) — synthesizes the translated text back into spoken audio
Interim results stream back while the person is still speaking; final results are delivered once an utterance completes.

WebSocket endpoint. The SDK opens a persistent, bidirectional WebSocket (wss://) connection — audio flows up and results stream down. The SDK manages the socket for you based on the configuration:

v1 (region + key) — suitable for a single, known source language.
v2 "universal" endpoint — required for language identification, multilingual translation, and Live Interpreter. It is built using FromEndpoint rather than FromSubscription, e.g. wss://{region}.stt.speech.microsoft.com/speech/universal/v2.
Audio capture & packets. Audio is streamed to the service in small chunks (packets) as it is captured, rather than buffered whole. The default expected format is 16 kHz, 16-bit, mono PCM. Input can come from a microphone, a WAV file, a push stream (you push bytes as you receive them), or a pull stream (the SDK pulls from your callback). Recognition can run single-shot or, more commonly for translation, continuously.

Recognizing vs. synthesis. A TranslationRecognizer raises events you subscribe to:

Recognizing — interim, live partial results (source text plus in-progress translation); ideal for live captions.
Recognized — final, stable text with finalized translations.
Synthesizing — chunks of translated audio, when speech-to-speech output is enabled.
Canceled — error or end of stream, carrying an error code and details.
Translations are returned in a dictionary keyed by target language. For spoken output, you set a voice and handle the Synthesizing event to play or forward the audio. Live Interpreter is the premium speech-to-speech path — continuous language identification plus low-latency translated speech in a personal voice that preserves the speaker's tone (requires the v2 endpoint and gated Personal Voice access).

Authentication. You can authenticate with a subscription key and region/endpoint, or with Microsoft Entra ID (the identity needs the Cognitive Services User role).

Language identification. Provide candidate languages, or use an open range for no specified source language (multilingual / Live Interpreter). Target languages must use full BCP-47 locale codes (e.g. zh-CN, en-US) rather than bare codes.

### Resources & links
Speech translation overview — https://learn.microsoft.com/azure/ai-services/speech-service/speech-translation
How to translate speech (code walkthroughs) — https://learn.microsoft.com/azure/ai-services/speech-service/how-to-translate-speech
Quickstart — https://learn.microsoft.com/azure/ai-services/speech-service/get-started-speech-translation
Official SDK code samples (all languages) — https://github.com/Azure-Samples/cognitive-services-speech-sdk
Language & voice support (valid locale codes) — https://learn.microsoft.com/azure/ai-services/speech-service/language-support
Supported regions — https://learn.microsoft.com/azure/ai-services/speech-service/regions
Sample POC repository — https://github.com/bhavinbdoshi/SpeechTranslationPOC
Within the official samples repository, the translation samples are located under each language folder, for example:

C#: samples/csharp/dotnetcore/console/translation_samples.cs
C++: samples/cpp/windows/console/samples/translation_samples.cpp
Python: samples/python/console/translation_sample.py

## Prerequisites

- .NET 8 SDK
- Azure Speech Service resource
- Azure Entra ID (Azure AD) identity with **Cognitive Services User** role on the Speech resource
- For Live Interpreter: approved Personal Voice access (Question 20 on the application form)
- Browser with AudioWorklet support (Chrome, Edge recommended)

## Configuration

### appsettings.json

{ "AzureSpeech": { "Region": "your-region", "ResourceId": "/subscriptions/{sub-id}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{name}", "ResourceName": "your-speech-resource-name", "SubscriptionKey": "your-key-if-needed-for-personal-voice" } }

| Setting | Description |
|---------|-------------|
| `Region` | Azure region (e.g., `westcentralus`, `eastus`) |
| `ResourceId` | Full ARM resource ID of your Speech resource |
| `ResourceName` | Name of your Speech resource (the part before `.cognitiveservices.azure.com`) |
| `SubscriptionKey` | Speech resource key (only needed if AAD doesn't work for Live Interpreter) |

## Testing Locally

1. Clone the repository:

git clone https://github.com/bhavinbdoshi/SpeechTranslationPOC.git cd SpeechTranslationPOC


2. Update `appsettings.json` with your Azure Speech Service configuration.

3. Ensure your Azure identity has the **Cognitive Services User** role:

az role assignment create --assignee <your-identity> 
--role "Cognitive Services User" 
--scope <your-speech-resource-id>


4. Run the application:

dotnet run

5. Open **two browser tabs** (or two different browsers) to `https://localhost:5001`

6. In each tab:
   - Enter a **Session ID** (same in both tabs, e.g., `session-1`)
   - Enter a **Name**
   - Select **Translation Mode** (Standard or Personal Voice)
   - Select languages
   - Click **Join Session**

7. Once both users join, microphones open automatically and translation begins.

### Testing Tips

- Use **Chrome or Edge** for best AudioWorklet support
- Speak in clear, complete sentences for best results
- The first few seconds may have slightly higher latency while the recognizer warms up
- Use the "Listen to original voice" toggle to compare translated vs. original audio

## Deploying to Azure App Service

### Prerequisites

- Azure CLI installed
- An Azure App Service (Windows, **64-bit** -- required for Speech SDK)

### Steps

1. **Create the App Service** (if not already):


az webapp create --name <app-name> 
--resource-group <rg> 
--plan <plan-name> 
--runtime "dotnet:8"


2. **Set to 64-bit** (required for Speech SDK native libraries):

Go to Azure Portal > Your App Service > Configuration > General settings

or 

az webapp config set --name <app-name> 
--resource-group <rg> 
--use-32bit-worker-process false


3. **Enable WebSockets** (required for SignalR):

az webapp config set --name <app-name> 
--resource-group <rg> 
--web-sockets-enabled true


4. **Assign Managed Identity** and grant role:

az webapp identity assign --name <app-name> --resource-group <rg>

Get the principal ID from the output, then:

az role assignment create --assignee <principal-id> 
--role "Cognitive Services User" 
--scope <speech-resource-id>

5. **Set app settings**:


az webapp config appsettings set --name <app-name> 
--resource-group <rg> 
--settings 
AzureSpeech__Region="your-region" 
AzureSpeech__ResourceId="your-resource-id" 
AzureSpeech__ResourceName="your-resource-name"


6. **Enable logging**:

az webapp log config --name <app-name> 
--resource-group <rg> 
--application-logging filesystem 
--level information


7. **Deploy**:

dotnet publish -c Release -o ./publish cd publish zip -r ../deploy.zip . az webapp deploy --name <app-name> 
--resource-group <rg> 
--src-path ../deploy.zip


8. **View logs**:

az webapp log tail --name <app-name> --resource-group <rg>


## Known Limitations

- Maximum 2 users per session
- Live Interpreter (Personal Voice) requires separate Microsoft approval
- Live Interpreter may not support all language combinations
- Auto-detect with translation requires SDK 1.44+ and the v2 endpoint
- Audio playback may have slight delays depending on network conditions
- No persistence -- sessions are lost on app restart

## Tech Stack

- **Backend**: ASP.NET Core 8 Razor Pages
- **Real-time**: SignalR (WebSocket)
- **Speech**: Microsoft.CognitiveServices.Speech SDK 1.44+
- **Auth**: Azure.Identity (DefaultAzureCredential / Managed Identity)
- **Frontend**: Vanilla JavaScript, AudioWorklet API, Web Audio API

## License

This project is provided as-is for demonstration purposes. No license is granted for commercial use.

---

> **REMINDER: This is experimental code for testing and demonstration only. Not suitable for production use.**