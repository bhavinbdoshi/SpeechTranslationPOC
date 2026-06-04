"use strict";

var connection = new signalR.HubConnectionBuilder()
    .withUrl("/translationHub")
    .withAutomaticReconnect()
    .build();

var currentSessionId = null;
var audioContext = null;
var mediaStream = null;
var workletNode = null;
var isRecording = false;

// Audio playback -- single context, plays immediately
var playbackContext = null;

// -- DOM Elements ---------------------------------------------------------
var setupPanel = document.getElementById("setup-panel");
var sessionPanel = document.getElementById("session-panel");
var joinBtn = document.getElementById("joinBtn");
var leaveBtn = document.getElementById("leaveBtn");
var listenOriginalToggle = document.getElementById("listenOriginalToggle");
var listenLanguageLive = document.getElementById("listenLanguageLive");
var statusArea = document.getElementById("statusArea");
var partnerInfo = document.getElementById("partnerInfo");
var transcript = document.getElementById("transcript");

// -- Base64 Helpers -------------------------------------------------------

function uint8ArrayToBase64(bytes) {
    var binary = "";
    for (var i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

function base64ToUint8Array(base64) {
    var binary = atob(base64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

// -- SignalR Event Handlers ------------------------------------------------

connection.on("JoinedSession", function (sessionId, displayName) {
    currentSessionId = sessionId;
    document.getElementById("activeSessionId").textContent = sessionId;
    document.getElementById("activeUserName").textContent = displayName;
    setupPanel.classList.add("d-none");
    sessionPanel.classList.remove("d-none");
    setStatus("Connected. Waiting for partner...");
});

connection.on("UserJoined", function (displayName, speakLanguage) {
    partnerInfo.innerHTML = "<strong>" + escapeHtml(displayName) + "</strong> joined (speaks " + escapeHtml(speakLanguage) + ")";
    setStatus("Partner connected!");
    addTranscriptEntry("system", displayName + " joined the session.");
});

connection.on("UserLeft", function (displayName) {
    partnerInfo.innerHTML = "";
    setStatus("Partner disconnected. Waiting...");
    addTranscriptEntry("system", displayName + " left the session.");
    stopMicrophone();
});

// Partial text -- shows while speaker is still talking
connection.on("ReceivePartial", function (speakerName, recognizedText, partialTranslation) {
    updatePartialTranscript(speakerName, recognizedText, partialTranslation);
});

// Final text -- arrives when sentence is complete
connection.on("ReceiveText", function (speakerName, recognizedText, translatedText) {
    finalizeTranscriptEntry(speakerName, recognizedText, translatedText);
});

// Translated audio -- arrives independently, play IMMEDIATELY
connection.on("ReceiveAudio", function (audioBase64, speakerName) {
    if (audioBase64 && audioBase64.length > 0) {
        var audioBytes = base64ToUint8Array(audioBase64);
        playAudioNow(audioBytes);
    }
});

// Original voice audio (raw PCM) -- for "listen original" mode
connection.on("ReceiveOriginalAudio", function (pcmBase64, speakerName) {
    if (pcmBase64 && pcmBase64.length > 0) {
        var audioBytes = base64ToUint8Array(pcmBase64);
        playPcmNow(audioBytes);
    }
});

connection.on("ListenModeChanged", function (listenOriginal) {
    setStatus(listenOriginal ? "Listening to original voice." : "Listening to translated voice.");
});

connection.on("ListenLanguageChanged", function (language) {
    setStatus("Now hearing translations in " + language + ".");
});

connection.on("Error", function (message) {
    setStatus("Error: " + message);
    alert(message);
});

// -- Join / Leave ---------------------------------------------------------

joinBtn.addEventListener("click", async function () {
    var sessionId = document.getElementById("sessionId").value.trim();
    var displayName = document.getElementById("displayName").value.trim();
    var speakLang = document.getElementById("speakLanguage").value;
    var listenLang = document.getElementById("listenLanguage").value;

    if (!sessionId || !displayName) {
        alert("Please enter a session ID and your name.");
        return;
    }

    listenLanguageLive.value = listenLang;

    // Create playback context during user gesture (will not be suspended)
    playbackContext = new (window.AudioContext || window.webkitAudioContext)();

    // Open mic while we have user gesture context
    await startMicrophone();

    if (!isRecording) {
        setStatus("Microphone is required to join a session.");
        if (playbackContext) {
            playbackContext.close();
            playbackContext = null;
        }
        return;
    }

    try {
        await connection.start();
        await connection.invoke("JoinSession", sessionId, displayName, speakLang, listenLang);
    } catch (err) {
        console.error("Failed to join:", err);
        setStatus("Connection failed. Check console.");
        stopMicrophone();
    }
});

leaveBtn.addEventListener("click", async function () {
    stopMicrophone();
    if (playbackContext) {
        playbackContext.close();
        playbackContext = null;
    }
    await connection.stop();
    currentSessionId = null;
    sessionPanel.classList.add("d-none");
    setupPanel.classList.remove("d-none");
    transcript.innerHTML = '<p class="text-muted text-center">Waiting for conversation...</p>';
    partnerInfo.innerHTML = "";
});

// -- Listen Settings ------------------------------------------------------

listenOriginalToggle.addEventListener("change", function () {
    if (currentSessionId) {
        connection.invoke("SetListenMode", currentSessionId, listenOriginalToggle.checked);
    }
});

listenLanguageLive.addEventListener("change", function () {
    if (currentSessionId) {
        connection.invoke("SetListenLanguage", currentSessionId, listenLanguageLive.value);
    }
});

// -- Microphone -----------------------------------------------------------

async function startMicrophone() {
    if (isRecording) return;

    try {
        audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
        await audioContext.audioWorklet.addModule("/js/pcm-processor.js");

        mediaStream = await navigator.mediaDevices.getUserMedia({
            audio: {
                sampleRate: 16000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true
            }
        });

        var source = audioContext.createMediaStreamSource(mediaStream);
        workletNode = new AudioWorkletNode(audioContext, "pcm-processor");

        workletNode.port.onmessage = function (event) {
            if (!isRecording || !currentSessionId) return;
            var chunk = new Uint8Array(event.data);
            var base64 = uint8ArrayToBase64(chunk);
            connection.invoke("SendAudio", currentSessionId, base64)
                .catch(function (err) { console.error("Send audio error:", err); });
        };

        source.connect(workletNode);
        workletNode.connect(audioContext.destination);

        isRecording = true;
        setStatus("Microphone active. Speak naturally!");
    } catch (err) {
        console.error("Microphone error:", err);
        setStatus("Microphone access denied. Please allow microphone permissions.");
    }
}

function stopMicrophone() {
    isRecording = false;

    if (workletNode) {
        workletNode.port.postMessage("stop");
        workletNode.disconnect();
        workletNode = null;
    }
    if (mediaStream) {
        mediaStream.getTracks().forEach(function (t) { t.stop(); });
        mediaStream = null;
    }
    if (audioContext) {
        audioContext.close();
        audioContext = null;
    }

    setStatus("Microphone stopped.");
}

// -- Audio Playback (immediate, no queue) ---------------------------------

function playAudioNow(audioBytes) {
    if (!playbackContext || playbackContext.state === "closed") return;

    if (playbackContext.state === "suspended") {
        playbackContext.resume().then(function () {
            playAudioNow(audioBytes);
        });
        return;
    }

    var arrayBuffer = audioBytes.buffer.slice(
        audioBytes.byteOffset,
        audioBytes.byteOffset + audioBytes.byteLength
    );

    // Try to decode as WAV/MP3 first
    try {
        playbackContext.decodeAudioData(arrayBuffer, function (decoded) {
            var source = playbackContext.createBufferSource();
            source.buffer = decoded;
            source.connect(playbackContext.destination);
            source.start();
        }, function (err) {
            // Fallback: treat as raw 16-bit PCM
            console.warn("decodeAudioData failed, playing as PCM:", err);
            playPcmNow(audioBytes);
        });
    } catch (err) {
        console.warn("decodeAudioData threw, playing as PCM:", err);
        playPcmNow(audioBytes);
    }
}

function playPcmNow(audioBytes) {
    if (!playbackContext || playbackContext.state === "closed") return;

    var rawBuffer = audioBytes.buffer.slice(
        audioBytes.byteOffset,
        audioBytes.byteOffset + audioBytes.byteLength
    );
    var int16 = new Int16Array(rawBuffer);
    var float32 = new Float32Array(int16.length);
    for (var i = 0; i < int16.length; i++) {
        float32[i] = int16[i] / 32768.0;
    }
    var buffer = playbackContext.createBuffer(1, float32.length, 16000);
    buffer.getChannelData(0).set(float32);
    var source = playbackContext.createBufferSource();
    source.buffer = buffer;
    source.connect(playbackContext.destination);
    source.start();
}

// -- Transcript Helpers ---------------------------------------------------

var partialEntry = null;

function updatePartialTranscript(speaker, recognizedText, partialTranslation) {
    var placeholder = transcript.querySelector(".text-muted.text-center");
    if (placeholder) placeholder.remove();

    if (!partialEntry) {
        partialEntry = document.createElement("div");
        partialEntry.className = "mb-2 p-2 rounded";
        partialEntry.style.background = "#fff3cd";
        transcript.appendChild(partialEntry);
    }

    partialEntry.innerHTML =
        "<strong>" + escapeHtml(speaker) + ":</strong>" +
        '<div class="small text-muted fst-italic">[speaking] ' + escapeHtml(recognizedText) + "</div>" +
        '<div class="small text-warning-emphasis">[translating] ' + escapeHtml(partialTranslation) + "</div>";
    transcript.scrollTop = transcript.scrollHeight;
}

function finalizeTranscriptEntry(speaker, originalText, translatedText) {
    if (partialEntry) {
        partialEntry.remove();
        partialEntry = null;
    }
    addTranscriptEntry(speaker, originalText, translatedText);
}

function addTranscriptEntry(speaker, originalText, translatedText) {
    var placeholder = transcript.querySelector(".text-muted.text-center");
    if (placeholder) placeholder.remove();

    var entry = document.createElement("div");
    entry.className = "mb-2 p-2 rounded";

    if (speaker === "system") {
        entry.className += " text-center text-muted small fst-italic";
        entry.textContent = originalText;
    } else {
        entry.style.background = "#e7f1ff";
        entry.innerHTML =
            "<strong>" + escapeHtml(speaker) + ":</strong>" +
            '<div class="small text-muted">Original: ' + escapeHtml(originalText) + "</div>" +
            (translatedText ? '<div class="small text-primary">Translated: ' + escapeHtml(translatedText) + "</div>" : "");
    }

    transcript.appendChild(entry);
    transcript.scrollTop = transcript.scrollHeight;
}

function setStatus(message) {
    statusArea.textContent = message;
}

function escapeHtml(text) {
    var div = document.createElement("div");
    div.textContent = text;
    return div.innerHTML;
}