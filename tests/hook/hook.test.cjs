'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const script = path.join(__dirname, '..', '..', 'src', 'AIUsageMonitor.Core', 'Hooks', 'hook.cjs');
const { buildLine } = require(script);

test('maps a Claude Notification payload', () => {
  const line = buildLine('claude', {
    hook_event_name: 'Notification', session_id: 's1', cwd: 'C:\\p\\demo',
    notification_type: 'permission_prompt', message: 'Bash needs approval',
  });
  const obj = JSON.parse(line);
  assert.equal(obj.agent, 'claude');
  assert.equal(obj.event, 'Notification');
  assert.equal(obj.session_id, 's1');
  assert.equal(obj.cwd, 'C:\\p\\demo');
  assert.equal(obj.notification_type, 'permission_prompt');
  assert.equal(obj.message, 'Bash needs approval');
  assert.equal(obj.source, null);
  assert.match(obj.ts, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
});

test('uses last_assistant_message for Stop and keeps source for SessionStart', () => {
  const stop = JSON.parse(buildLine('codex', { hook_event_name: 'Stop', session_id: 's2', cwd: '/w', last_assistant_message: 'All done.' }));
  assert.equal(stop.message, 'All done.');
  const start = JSON.parse(buildLine('claude', { hook_event_name: 'SessionStart', session_id: 's3', cwd: '/w', source: 'resume' }));
  assert.equal(start.source, 'resume');
});

test('drops events fired inside a subagent context, except SubagentStart/SubagentStop', () => {
  assert.equal(buildLine('claude', { hook_event_name: 'Stop', session_id: 's1', agent_id: 'a1' }), null);
  assert.equal(buildLine('claude', { hook_event_name: 'PreToolUse', session_id: 's1', agent_id: 'a1' }), null);
  const start = JSON.parse(buildLine('claude', { hook_event_name: 'SubagentStart', session_id: 's1', cwd: 'C:\\p', agent_id: 'a1', agent_type: 'Explore' }));
  assert.equal(start.event, 'SubagentStart');
  assert.equal(start.session_id, 's1');
  assert.equal(start.agent_id, 'a1');
  assert.equal(start.agent_type, 'Explore');
  const stop = JSON.parse(buildLine('codex', { hook_event_name: 'SubagentStop', session_id: 's2', agent_id: 'b7', last_assistant_message: 'done' }));
  assert.equal(stop.event, 'SubagentStop');
  assert.equal(stop.agent_id, 'b7');
  assert.equal(stop.agent_type, null);
  assert.equal(stop.message, 'done');
});

test('regular events carry agent_id null', () => {
  const obj = JSON.parse(buildLine('claude', { hook_event_name: 'Stop', session_id: 's1' }));
  assert.equal(obj.agent_id, null);
  assert.equal(obj.agent_type, null);
});

test('skips unknown agents and malformed payloads', () => {
  assert.equal(buildLine('gemini', { hook_event_name: 'Stop', session_id: 's1' }), null);
  assert.equal(buildLine('claude', null), null);
  assert.equal(buildLine('claude', 'not an object'), null);
  assert.equal(buildLine('claude', { session_id: 's1' }), null);
  assert.equal(buildLine('claude', { hook_event_name: 'Stop' }), null);
});

test('truncates long messages to 200 chars and ignores non-string fields', () => {
  const obj = JSON.parse(buildLine('codex', { hook_event_name: 'Stop', session_id: 's2', last_assistant_message: 'x'.repeat(500), cwd: 42, source: { subagent: 'x' } }));
  assert.equal(obj.message.length, 200);
  assert.equal(obj.cwd, null);
  assert.equal(obj.source, null);
});

test('end to end: appends one line to the events file and exits 0', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'aium-hook-'));
  const eventsFile = path.join(dir, 'nested', 'events.jsonl');
  const payload = { hook_event_name: 'UserPromptSubmit', session_id: 'e2e', cwd: dir, prompt: 'hi' };
  const run = spawnSync(process.execPath, [script, 'codex'], { input: JSON.stringify(payload), env: { ...process.env, AIUM_EVENTS_FILE: eventsFile } });
  assert.equal(run.status, 0, run.stderr.toString());
  const lines = fs.readFileSync(eventsFile, 'utf8').trim().split('\n');
  assert.equal(lines.length, 1);
  assert.equal(JSON.parse(lines[0]).event, 'UserPromptSubmit');
  fs.rmSync(dir, { recursive: true, force: true });
});

test('end to end: garbage on stdin still exits 0 and writes nothing', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'aium-hook-'));
  const eventsFile = path.join(dir, 'events.jsonl');
  const run = spawnSync(process.execPath, [script, 'claude'], { input: '{not json', env: { ...process.env, AIUM_EVENTS_FILE: eventsFile } });
  assert.equal(run.status, 0);
  assert.equal(fs.existsSync(eventsFile), false);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('truncation never leaves a lone surrogate', () => {
  const obj = JSON.parse(buildLine('codex', {
    hook_event_name: 'Stop', session_id: 's4',
    last_assistant_message: 'a'.repeat(199) + '\u{1F600}' + 'tail',
  }));
  assert.equal(obj.message.length, 199);
  for (let i = 0; i < obj.message.length; i++) {
    const c = obj.message.charCodeAt(i);
    assert.ok(c < 0xD800 || c > 0xDFFF, `lone surrogate at index ${i}`);
  }
});

test('truncation keeps a complete surrogate pair that fits', () => {
  const obj = JSON.parse(buildLine('codex', {
    hook_event_name: 'Stop', session_id: 's5',
    last_assistant_message: 'a'.repeat(198) + '\u{1F600}' + 'tail',
  }));
  assert.equal(obj.message.length, 200);
  assert.equal(obj.message.slice(198), '\u{1F600}');
});
