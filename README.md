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

Browser (User A)                    Server                         Browser (User B) |                                |                                  | |-- Join Session (SignalR) ----->|                                  | |                                |<----- Join Session (SignalR) ----| |                                |                                  | |                                |-- Create Translation Sessions --| |                                |   (A->B and B->A)               | |                                |                                  | |-- PCM Audio (200ms chunks) -->|                                  | |                                |-- WriteAudio() to SDK           | |                                |                                  | |                                |<-- SDK: Recognized event        | |                                |-- ReceiveText (SignalR) ------->| |                                |                                  | |                                |<-- SDK: Synthesizing event      | |                                |-- ReceiveAudio (SignalR) ------>| |                                |                                  |

## Translation Modes

### Standard (Neural Voice)

- User selects source and target language
- Translation uses Azure Neural Voice for synthesis (e.g., `en-US-JennyNeural`)
- Uses `SpeechTranslationConfig.FromAuthorizationToken` with Entra ID (AAD)

### Live Interpreter (Personal Voice)

- Auto-detects source language (no selection needed)
- Translation uses the speaker's own voice clone
- Uses `SpeechTranslationConfig.FromEndpoint` with the v2 universal endpoint
- **Requires separate access approval** at https://aka.ms/livechatinterpreter
- Voice name: `personal-voice`

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