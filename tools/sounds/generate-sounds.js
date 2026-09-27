#!/usr/bin/env node
'use strict';
// AIUsageMonitor notification sounds (spec 2026-09-27 §8).
// Usage: node tools/sounds/generate-sounds.js [output dir]   (default: src/AIUsageMonitor.App/Assets/Sounds)
// Writes done.wav and attention.wav as 16-bit PCM mono 44.1 kHz. Deterministic: V8 computes Math.sin, Math.cos and
// Math.exp with its fdlibm port and every sample is rounded to 16 bits, so a fresh run gives the committed bytes
// (generate-sounds.test.mjs checks it). No dependencies and no third-party audio: every sample comes from the
// parameters below.
const fs = require('node:fs');
const path = require('node:path');

const SAMPLE_RATE = 44100;
const FULL_SCALE = 32767;
const WAV_HEADER_BYTES = 44;

// A note is a sine with its 2nd harmonic, a linear attack and an exponential decay (time constant decayMs). A note
// with lengthMs stops there, after a raised-cosine release of releaseMs; one without rings until the end of the file.
// Each sound is normalised so that its loudest sample sits at peakDbfs; a sound with tailFadeMs fades its last
// tailFadeMs to an exact zero.
const SOUNDS = Object.freeze({
  done: Object.freeze({
    file: 'done.wav',
    durationMs: 520,
    peakDbfs: -14,
    tailFadeMs: 40,
    notes: Object.freeze([
      Object.freeze({ frequency: 659.26, startMs: 0, attackMs: 8, decayMs: 90, harmonic: 0.15 }), // E5
      Object.freeze({ frequency: 987.77, startMs: 110, attackMs: 8, decayMs: 90, harmonic: 0.15 }), // B5
    ]),
  }),
  attention: Object.freeze({
    file: 'attention.wav',
    durationMs: 420,
    peakDbfs: -10,
    tailFadeMs: 0,
    notes: Object.freeze([
      // Two 85 ms taps with a 55 ms gap between them.
      Object.freeze({ frequency: 987.77, startMs: 0, lengthMs: 85, releaseMs: 10, attackMs: 4, decayMs: 45, harmonic: 0.25 }), // B5
      Object.freeze({ frequency: 1318.51, startMs: 140, lengthMs: 85, releaseMs: 10, attackMs: 4, decayMs: 45, harmonic: 0.25 }), // E6
    ]),
  }),
});

function sampleCount(ms) {
  return Math.round((ms * SAMPLE_RATE) / 1000);
}

function envelope(note, t) {
  const attack = note.attackMs / 1000;
  let gain = t < attack ? t / attack : Math.exp(-(t - attack) / (note.decayMs / 1000));
  if (note.lengthMs !== undefined) {
    const length = note.lengthMs / 1000;
    if (t >= length) return 0;
    const release = note.releaseMs / 1000;
    const releaseStart = length - release;
    if (t > releaseStart) gain *= 0.5 * (1 + Math.cos((Math.PI * (t - releaseStart)) / release));
  }
  return gain;
}

function tone(note, t) {
  const phase = 2 * Math.PI * note.frequency * t;
  return Math.sin(phase) + note.harmonic * Math.sin(2 * phase);
}

/** The sound as 16-bit signed samples (Int16Array), normalised to its peak. */
function render(sound) {
  const count = sampleCount(sound.durationMs);
  const mix = new Float64Array(count);
  for (const note of sound.notes) {
    const first = sampleCount(note.startMs);
    for (let i = first; i < count; i++) {
      const t = (i - first) / SAMPLE_RATE;
      mix[i] += envelope(note, t) * tone(note, t);
    }
  }
  const fade = sampleCount(sound.tailFadeMs);
  for (let k = 0; k < fade; k++) {
    const x = fade === 1 ? 1 : k / (fade - 1);
    mix[count - fade + k] *= 0.5 * (1 + Math.cos(Math.PI * x));
  }
  let peak = 0;
  for (const value of mix) peak = Math.max(peak, Math.abs(value));
  const scale = peak > 0 ? (FULL_SCALE * Math.pow(10, sound.peakDbfs / 20)) / peak : 0;
  const samples = new Int16Array(count);
  for (let i = 0; i < count; i++) {
    const value = Math.round(mix[i] * scale);
    samples[i] = Math.max(-FULL_SCALE, Math.min(FULL_SCALE, value));
  }
  return samples;
}

/** Canonical 44-byte RIFF/WAVE header (PCM, mono, 16-bit) followed by the little-endian samples. */
function encodeWav(samples) {
  const dataBytes = samples.length * 2;
  const buffer = Buffer.alloc(WAV_HEADER_BYTES + dataBytes);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + dataBytes, 4);
  buffer.write('WAVE', 8, 'ascii');
  buffer.write('fmt ', 12, 'ascii');
  buffer.writeUInt32LE(16, 16); // fmt chunk size
  buffer.writeUInt16LE(1, 20); // PCM
  buffer.writeUInt16LE(1, 22); // mono
  buffer.writeUInt32LE(SAMPLE_RATE, 24);
  buffer.writeUInt32LE(SAMPLE_RATE * 2, 28); // byte rate
  buffer.writeUInt16LE(2, 32); // block align
  buffer.writeUInt16LE(16, 34); // bits per sample
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(dataBytes, 40);
  for (let i = 0; i < samples.length; i++) buffer.writeInt16LE(samples[i], WAV_HEADER_BYTES + i * 2);
  return buffer;
}

/** Writes every sound into dir and returns what was written, for the report. */
function writeSounds(dir) {
  fs.mkdirSync(dir, { recursive: true });
  return Object.values(SOUNDS).map((sound) => {
    const samples = render(sound);
    const bytes = encodeWav(samples);
    const file = path.join(dir, sound.file);
    fs.writeFileSync(file, bytes);
    let peak = 0;
    for (const value of samples) peak = Math.max(peak, Math.abs(value));
    return {
      file,
      bytes: bytes.length,
      durationMs: (samples.length * 1000) / SAMPLE_RATE,
      peakDbfs: 20 * Math.log10(peak / FULL_SCALE),
    };
  });
}

const defaultOutputDir = path.join(__dirname, '..', '..', 'src', 'AIUsageMonitor.App', 'Assets', 'Sounds');

if (require.main === module) {
  for (const written of writeSounds(process.argv[2] ?? defaultOutputDir)) {
    console.log(`${path.basename(written.file)}: ${written.bytes} bytes, ${written.durationMs.toFixed(0)} ms, ` +
      `peak ${written.peakDbfs.toFixed(2)} dBFS`);
  }
}

module.exports = { SAMPLE_RATE, FULL_SCALE, SOUNDS, render, encodeWav, writeSounds, defaultOutputDir };
