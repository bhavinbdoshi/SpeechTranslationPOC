"use strict";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/translationHub")
    .withAutomaticReconnect()
    .build();

let currentSessionId = null;
let audioContext = null;
let mediaStream = null;
let workletNode = null;
let isRecording = false;

// ??? DOM Elements ????????????????????????????????????????????????
const setupPanel = document.getElementById("setup-panel");
const sessionPanel = document.getElementById("session-panel");
const joinBtn = document.getElementById("joinBtn");
const leaveBtn = document.getElementById("leaveBtn");
const startMicBtn = document.getElementById("startMicBtn");
const stopMicBtn = document.getElementById("stopMicBtn");
const listenOriginalToggle = document.getElementById("listenOriginalToggle");
const listenLanguageLive = document.getElementById("listenLanguageLive");
const statusArea = document.getElementById("statusArea");
const partnerInfo = document.getElementById("partnerInfo");
const transcript = document.getElementById("transcript");

// ??? Base64 Helpers ??????????????????????????????????????????????

function uint8ArrayToBase64(bytes) {
    let binary = "";
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

function base64ToUint8Array(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

// ??? SignalR Event Handlers ??????????????????????????????????????

connection.on("JoinedSession", (sessionId, displayName) => {
    currentSessionId = sessionId;
    document.getElementById("activeSessionId").textContent = sessionId;
    document.getElementById("activeUserName").textContent = displayName;
    setupPanel.classList.add("d-none");
    sessionPanel.classList.remove("d-none");
    setStatus("Connected. Waiting for partner...");
});

connection.on("UserJoined", (displayName, speakLanguage) => {
    partnerInfo.innerHTML = `<strong>${escapeHtml(displayName)}</strong> joined (speaks ${escapeHtml(speakLanguage)})`;
    setStatus("Partner connected! You can start speaking.");
    addTranscriptEntry("system", `${displayName} joined the session.`);
});

connection.on("UserLeft", (displayName) => {
    partnerInfo.innerHTML = "";
    setStatus("Partner disconnected. Waiting...");
    addTranscriptEntry("system", `${displayName} left the session.`);
});

connection.on("ReceiveAudio", (audioBase64, speakerName, recognizedText, translatedText, isOriginal) => {
    if (audioBase64 && audioBase64.length > 0) {
        const audioBytes = base64ToUint8Array(audioBase64);
        playAudio(audioBytes, isOriginal);
    }

    if (!isOriginal && recognizedText) {
        addTranscriptEntry(speakerName, recognizedText, translatedText);
    }
});

connection.on("ListenModeChanged", (listenOriginal) => {
    setStatus(listenOriginal ? "Listening to original voice." : "Listening to translated voice.");
});

connection.on("ListenLanguageChanged", (language) => {
    setStatus(`Now hearing translations in ${language}.`);
});

connection.on("Error", (message) => {
    setStatus(`Error: ${message}`);
    alert(message);
});

// ??? Join / Leave ????????????????????????????????????????????????

joinBtn.addEventListener("click", async () => {
    const sessionId = document.getElementById("sessionId").value.trim();
    const displayName = document.getElementById("displayName").value.trim();
    const speakLang = document.getElementById("speakLanguage").value;
    const listenLang = document.getElementById("listenLanguage").value;

    if (!sessionId || !displayName) {
        alert("Please enter a session ID and your name.");
        return;
    }

    listenLanguageLive.value = listenLang;

    try {
        await connection.start();
        await connection.invoke("JoinSession", sessionId, displayName, speakLang, listenLang);
    } catch (err) {
        console.error("Failed to join:", err);
        setStatus("Connection failed. Check console.");
    }
});

leaveBtn.addEventListener("click", async () => {
    stopRecording();
    await connection.stop();
    currentSessionId = null;
    sessionPanel.classList.add("d-none");
    setupPanel.classList.remove("d-none");
    transcript.innerHTML = '<p class="text-muted text-center">Waiting for conversation...</p>';
    partnerInfo.innerHTML = "";
});

// ??? Listen Settings ?????????????????????????????????????????????

listenOriginalToggle.addEventListener("change", () => {
    if (currentSessionId) {
        connection.invoke("SetListenMode", currentSessionId, listenOriginalToggle.checked);
    }
});

listenLanguageLive.addEventListener("change", () => {
    if (currentSessionId) {
        connection.invoke("SetListenLanguage", currentSessionId, listenLanguageLive.value);
    }
});

// ??? Microphone Recording (AudioWorklet) ?????????????????????????

startMicBtn.addEventListener("click", async () => {
    await startRecording();
});

stopMicBtn.addEventListener("click", () => {
    stopRecording();
});

async function startRecording() {
    try {
        audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });

        // Load the AudioWorklet processor module
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

        const source = audioContext.createMediaStreamSource(mediaStream);

        workletNode = new AudioWorkletNode(audioContext, "pcm-processor");

        // Receive PCM chunks from the worklet thread and send as base64
        workletNode.port.onmessage = (event) => {
            if (!isRecording || !currentSessionId) return;

            const pcmBuffer = event.data; // ArrayBuffer
            const chunk = new Uint8Array(pcmBuffer);
            const base64 = uint8ArrayToBase64(chunk);

            connection.invoke("SendAudio", currentSessionId, base64)
                .catch(err => console.error("Send audio error:", err));
        };

        source.connect(workletNode);
        workletNode.connect(audioContext.destination);

        isRecording = true;
        startMicBtn.classList.add("d-none");
        stopMicBtn.classList.remove("d-none");
        setStatus("?? Recording... Speak now!");
    } catch (err) {
        console.error("Microphone error:", err);
        setStatus("Microphone access denied. Please allow microphone permissions.");
    }
}

function stopRecording() {
    isRecording = false;

    if (workletNode) {
        workletNode.port.postMessage("stop");
        workletNode.disconnect();
        workletNode = null;
    }
    if (mediaStream) {
        mediaStream.getTracks().forEach(track => track.stop());
        mediaStream = null;
    }
    if (audioContext) {
        audioContext.close();
        audioContext = null;
    }

    startMicBtn.classList.remove("d-none");
    stopMicBtn.classList.add("d-none");
    setStatus("Microphone stopped.");
}

// ??? Audio Playback ??????????????????????????????????????????????

function playAudio(audioBytes, isOriginalPcm) {
    try {
        const playbackContext = new (window.AudioContext || window.webkitAudioContext)();

        if (isOriginalPcm) {
            const int16Array = new Int16Array(audioBytes.buffer);
            const float32 = new Float32Array(int16Array.length);
            for (let i = 0; i < int16Array.length; i++) {
                float32[i] = int16Array[i] / 32768.0;
            }
            const buffer = playbackContext.createBuffer(1, float32.length, 16000);
            buffer.getChannelData(0).set(float32);
            const source = playbackContext.createBufferSource();
            source.buffer = buffer;
            source.connect(playbackContext.destination);
            source.start();
            source.onended = () => playbackContext.close();
        } else {
            playbackContext.decodeAudioData(audioBytes.buffer, (decodedBuffer) => {
                const source = playbackContext.createBufferSource();
                source.buffer = decodedBuffer;
                source.connect(playbackContext.destination);
                source.start();
                source.onended = () => playbackContext.close();
            }, (err) => {
                console.warn("Could not decode synthesized audio:", err);
                playbackContext.close();
            });
        }
    } catch (err) {
        console.error("Playback error:", err);
    }
}

// ??? Helpers ?????????????????????????????????????????????????????

function setStatus(message) {
    statusArea.textContent = message;
}

function addTranscriptEntry(speaker, originalText, translatedText) {
    const placeholder = transcript.querySelector(".text-muted.text-center");
    if (placeholder) placeholder.remove();

    const entry = document.createElement("div");
    entry.className = "mb-2 p-2 rounded";

    if (speaker === "system") {
        entry.className += " text-center text-muted small fst-italic";
        entry.textContent = originalText;
    } else {
        entry.style.background = "#e7f1ff";
        entry.innerHTML = `
            <strong>${escapeHtml(speaker)}:</strong>
            <div class="small text-muted">Original: ${escapeHtml(originalText)}</div>
            ${translatedText ? `<div class="small text-primary">Translated: ${escapeHtml(translatedText)}</div>` : ""}
        `;
    }

    transcript.appendChild(entry);
    transcript.scrollTop = transcript.scrollHeight;
}

function escapeHtml(text) {
    const div = document.createElement("div");
    div.textContent = text;
    return div.innerHTML;
}