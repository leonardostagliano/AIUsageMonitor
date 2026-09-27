import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import sounds from './generate-sounds.js';

const { SAMPLE_RATE, SOUNDS, render, encodeWav, writeSounds } = sounds;
const committed = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'AIUsageMonitor.App', 'Assets', 'Sounds');

const at = (ms) => Math.round((ms * SAMPLE_RATE) / 1000);

// Power of one frequency over samples[from, to) (Goertzel): enough to tell which note sounds in a window.
function power(samples, frequency, fromMs, toMs) {
  const coefficient = 2 * Math.cos((2 * Math.PI * frequency) / SAMPLE_RATE);
  let previous = 0;
  let beforePrevious = 0;
  for (let i = at(fromMs); i < at(toMs); i++) {
    const current = samples[i] + coefficient * previous - beforePrevious;
    beforePrevious = previous;
    previous = current;
  }
  return previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious;
}

describe('generate-sounds', () => {
  let dir;
  before(() => {
    dir = mkdtempSync(join(tmpdir(), 'aium-sounds-'));
    writeSounds(dir);
  });
  after(() => rmSync(dir, { recursive: true, force: true }));

  it('regenerates the committed files byte for byte', () => {
    for (const sound of Object.values(SOUNDS)) {
      const fresh = readFileSync(join(dir, sound.file));
      const shipped = readFileSync(join(committed, sound.file));
      assert.ok(fresh.equals(shipped), `${sound.file} differs from a fresh run: run node tools/sounds/generate-sounds.js and commit it`);
    }
  });

  it('writes the header that the WAV readers expect', () => {
    const wav = encodeWav(Int16Array.from([0, 1000, -1000]));
    assert.equal(wav.length, 44 + 6);
    assert.equal(wav.toString('ascii', 0, 4), 'RIFF');
    assert.equal(wav.readUInt32LE(4), 36 + 6);
    assert.equal(wav.toString('ascii', 8, 16), 'WAVEfmt ');
    assert.deepEqual([wav.readUInt16LE(20), wav.readUInt16LE(22), wav.readUInt32LE(24), wav.readUInt16LE(34)], [1, 1, 44100, 16]);
    assert.equal(wav.toString('ascii', 36, 40), 'data');
    assert.equal(wav.readUInt32LE(40), 6);
    assert.equal(wav.readInt16LE(46), 1000);
    assert.equal(wav.readInt16LE(48), -1000);
  });

  it('plays E5 and then B5 in done', () => {
    const done = render(SOUNDS.done);
    // Before the second note (110 ms) only E5 sounds; once it has started B5 is the louder one.
    assert.ok(power(done, 659.26, 0, 100) > 100 * power(done, 987.77, 0, 100));
    assert.ok(power(done, 987.77, 120, 220) > power(done, 659.26, 120, 220));
  });

  it('starts done from silence and fades its tail to an exact zero', () => {
    const done = render(SOUNDS.done);
    assert.equal(done.length, at(520));
    assert.equal(done[0], 0);
    assert.equal(done[done.length - 1], 0);
  });

  it('plays two separate taps, B5 and then E6, in attention', () => {
    const attention = render(SOUNDS.attention);
    assert.equal(attention.length, at(420));
    assert.ok(power(attention, 987.77, 0, 85) > 100 * power(attention, 1318.51, 0, 85));
    assert.ok(power(attention, 1318.51, 140, 225) > 100 * power(attention, 987.77, 140, 225));
    // The 55 ms gap and everything after the second tap are silent.
    assert.ok(attention.subarray(at(85), at(140)).every((sample) => sample === 0));
    assert.ok(attention.subarray(at(225)).every((sample) => sample === 0));
  });

  it('makes attention 4 dB louder than done', () => {
    const peak = (samples) => samples.reduce((max, sample) => Math.max(max, Math.abs(sample)), 0);
    const difference = 20 * Math.log10(peak(render(SOUNDS.attention)) / peak(render(SOUNDS.done)));
    assert.ok(Math.abs(difference - 4) < 0.05, `attention is ${difference.toFixed(2)} dB louder than done`);
  });
});
