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
    transcript_path: 'C:\\Users\\demo\\.claude\\projects\\p\\s1.jsonl',
  });
  const obj = JSON.parse(line);
  assert.equal(obj.agent, 'claude');
  assert.equal(obj.event, 'Notification');
  assert.equal(obj.session_id, 's1');
  assert.equal(obj.cwd, 'C:\\p\\demo');
  assert.equal(obj.notification_type, 'permission_prompt');
  assert.equal(obj.message, 'Bash needs approval');
  assert.equal(obj.source, null);
  assert.equal(obj.transcript_path, 'C:\\Users\\demo\\.claude\\projects\\p\\s1.jsonl');
  assert.equal(obj.agent_transcript_path, null);
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
  assert.equal(start.agent_transcript_path, null);
  const stop = JSON.parse(buildLine('codex', {
    hook_event_name: 'SubagentStop', session_id: 's2', agent_id: 'b7', last_assistant_message: 'done',
    agent_transcript_path: '/home/demo/.claude/projects/p/s2/subagents/agent-b7.jsonl',
  }));
  assert.equal(stop.event, 'SubagentStop');
  assert.equal(stop.agent_id, 'b7');
  assert.equal(stop.agent_type, null);
  assert.equal(stop.message, 'done');
  assert.equal(stop.agent_transcript_path, '/home/demo/.claude/projects/p/s2/subagents/agent-b7.jsonl');
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

test('transcript_path is only kept when a string, agent_transcript_path only fires on SubagentStop', () => {
  const nonString = JSON.parse(buildLine('claude', { hook_event_name: 'Stop', session_id: 's1', transcript_path: 42 }));
  assert.equal(nonString.transcript_path, null);
  const noField = JSON.parse(buildLine('claude', { hook_event_name: 'Stop', session_id: 's1' }));
  assert.equal(noField.transcript_path, null);
  // SubagentStart also does not surface agent_transcript_path, only SubagentStop does.
  const start = JSON.parse(buildLine('claude', { hook_event_name: 'SubagentStart', session_id: 's1', agent_id: 'a1', agent_transcript_path: '/should/not/appear.jsonl' }));
  assert.equal(start.agent_transcript_path, null);
  const stopBadType = JSON.parse(buildLine('claude', { hook_event_name: 'SubagentStop', session_id: 's1', agent_id: 'a1', agent_transcript_path: 99 }));
  assert.equal(stopBadType.agent_transcript_path, null);
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

test('SessionStart carries a host object read from the environment and ppid', () => {
  const saved = { HERDR_PANE_ID: process.env.HERDR_PANE_ID, WT_SESSION: process.env.WT_SESSION, TERM_PROGRAM: process.env.TERM_PROGRAM, VSCODE_PID: process.env.VSCODE_PID, WMUX_PTY_ID: process.env.WMUX_PTY_ID };
  try {
    process.env.HERDR_PANE_ID = 'w15:p1';
    process.env.WT_SESSION = '4b2c1234-0000-0000-0000-000000000000';
    delete process.env.TERM_PROGRAM;
    delete process.env.VSCODE_PID;
    delete process.env.WMUX_PTY_ID;
    const obj = JSON.parse(buildLine('claude', { hook_event_name: 'SessionStart', session_id: 's1', cwd: '/w' }));
    assert.ok(Number.isInteger(obj.host.ppid) && obj.host.ppid > 0);
    assert.equal(obj.host.herdr_pane, 'w15:p1');
    assert.equal(obj.host.wt_session, '4b2c1234-0000-0000-0000-000000000000');
    assert.equal(obj.host.term_program, null);
    assert.equal(obj.host.vscode_pid, null);
    assert.equal(obj.host.wmux_pty, null);

    const prompt = JSON.parse(buildLine('claude', { hook_event_name: 'UserPromptSubmit', session_id: 's1', cwd: '/w' }));
    assert.equal(prompt.host.herdr_pane, 'w15:p1');
  } finally {
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  }
});

test('inside wmux the host carries the pty id of the pane', () => {
  const saved = { TERM_PROGRAM: process.env.TERM_PROGRAM, WMUX_PTY_ID: process.env.WMUX_PTY_ID };
  try {
    process.env.TERM_PROGRAM = 'wmux';
    process.env.WMUX_PTY_ID = 'daemon-8bab41b8';
    const obj = JSON.parse(buildLine('claude', { hook_event_name: 'UserPromptSubmit', session_id: 's1', cwd: '/w' }));
    assert.equal(obj.host.term_program, 'wmux');
    assert.equal(obj.host.wmux_pty, 'daemon-8bab41b8');
  } finally {
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  }
});

test('host is null for events other than SessionStart/UserPromptSubmit', () => {
  const obj = JSON.parse(buildLine('claude', { hook_event_name: 'Stop', session_id: 's1', cwd: '/w' }));
  assert.equal(obj.host, null);
});

test('a non-numeric VSCODE_PID maps to a null vscode_pid, a numeric one is parsed', () => {
  const saved = process.env.VSCODE_PID;
  try {
    process.env.VSCODE_PID = 'not-a-number';
    const nonNumeric = JSON.parse(buildLine('claude', { hook_event_name: 'SessionStart', session_id: 's1' }));
    assert.equal(nonNumeric.host.vscode_pid, null);

    process.env.VSCODE_PID = '4242';
    const numeric = JSON.parse(buildLine('claude', { hook_event_name: 'SessionStart', session_id: 's1' }));
    assert.equal(numeric.host.vscode_pid, 4242);
  } finally {
    if (saved === undefined) delete process.env.VSCODE_PID; else process.env.VSCODE_PID = saved;
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
