"use strict";

/**
 * AudioWorkletProcessor that captures PCM audio and sends small 200ms chunks
 * continuously. No silence detection needed because the Azure Speech SDK handles
 * speech boundary detection internally via continuous recognition.
 */
class PcmProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._buffer = [];
        this._chunkSize = 6400; // 200ms at 16kHz, 16-bit, mono = 6400 bytes
        this._active = true;

        this.port.onmessage = (event) => {
            if (event.data === "stop") {
                this._active = false;
                this._flush();
            }
        };
    }

    process(inputs) {
        if (!this._active) return false;

        const input = inputs[0];
        if (!input || !input[0] || input[0].length === 0) return true;

        const channelData = input[0];

        // Convert float32 samples to int16 PCM bytes (little-endian)
        for (let i = 0; i < channelData.length; i++) {
            let s = Math.max(-1, Math.min(1, channelData[i]));
            s = s < 0 ? s * 0x8000 : s * 0x7FFF;
            this._buffer.push(s & 0xFF, (s >> 8) & 0xFF);
        }

        // Emit a chunk every 200ms worth of audio
        while (this._buffer.length >= this._chunkSize) {
            const chunk = new Uint8Array(this._buffer.splice(0, this._chunkSize));
            this.port.postMessage(chunk.buffer, [chunk.buffer]);
        }

        return true;
    }

    _flush() {
        if (this._buffer.length === 0) return;
        const chunk = new Uint8Array(this._buffer);
        this._buffer = [];
        this.port.postMessage(chunk.buffer, [chunk.buffer]);
    }
}

registerProcessor("pcm-processor", PcmProcessor);