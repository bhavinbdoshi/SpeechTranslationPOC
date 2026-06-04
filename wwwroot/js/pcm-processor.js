"use strict";

/**
 * AudioWorkletProcessor that captures PCM audio from the microphone,
 * converts float32 samples to int16, buffers them, and sends chunks
 * to the main thread when silence is detected after speech.
 */
class PcmProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._buffer = [];
        this._silenceCount = 0;
        this._silenceThreshold = 0.01;
        this._minBufferSize = 32000;  // ~2 seconds at 16kHz (in bytes)
        this._maxBufferSize = 160000; // ~10 seconds safety cap
        this._active = true;

        this.port.onmessage = (event) => {
            if (event.data === "stop") {
                this._active = false;
            }
        };
    }

    process(inputs) {
        if (!this._active) return false;

        const input = inputs[0];
        if (!input || !input[0] || input[0].length === 0) return true;

        const channelData = input[0];

        // Check for silence
        let sum = 0;
        for (let i = 0; i < channelData.length; i++) {
            sum += Math.abs(channelData[i]);
        }
        const average = sum / channelData.length;

        if (average < this._silenceThreshold) {
            this._silenceCount++;
        } else {
            this._silenceCount = 0;
        }

        // Convert float32 to int16 PCM bytes
        for (let i = 0; i < channelData.length; i++) {
            let s = Math.max(-1, Math.min(1, channelData[i]));
            s = s < 0 ? s * 0x8000 : s * 0x7FFF;
            const lo = s & 0xFF;
            const hi = (s >> 8) & 0xFF;
            this._buffer.push(lo, hi);
        }

        // Send when silence detected after enough speech
        if (this._buffer.length >= this._minBufferSize && this._silenceCount >= 3) {
            this._flush();
        }

        // Safety: prevent unbounded growth
        if (this._buffer.length > this._maxBufferSize) {
            this._flush();
        }

        return true;
    }

    _flush() {
        if (this._buffer.length === 0) return;
        const chunk = new Uint8Array(this._buffer);
        this._buffer = [];
        this._silenceCount = 0;
        this.port.postMessage(chunk.buffer, [chunk.buffer]);
    }
}

registerProcessor("pcm-processor", PcmProcessor);