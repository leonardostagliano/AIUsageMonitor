#!/usr/bin/env node
'use strict';
// AIUsageMonitor hook bridge.
// Usage: node hook.cjs claude|codex   (agent hook payload on stdin)
// Appends one JSON line to ~/.aiusagemonitor/events.jsonl (or $AIUM_EVENTS_FILE) and ALWAYS exits 0.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const MAX_MESSAGE = 200;
// Claude Code lists the background work still in flight on Stop/SubagentStop; only agents and workflows matter to
// the monitor. Past this many entries the list is dropped (null, "unknown") rather than cut: a cut list would make the
// app mark the agents left out as finished.
const MAX_BACKGROUND_TASKS = 100;
const TRACKED_TASK_TYPES = new Set(['subagent', 'workflow']);

function str(value) {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function truncate(value) {
  const s = str(value);
  if (s === null || s.length <= MAX_MESSAGE) return s;
  let out = s.slice(0, MAX_MESSAGE);
  // Slicing on UTF-16 code units can cut a surrogate pair in half; a lone high
  // surrogate makes the JSON unreadable for strict UTF-16 consumers (.NET).
  const last = out.charCodeAt(out.length - 1);
  if (last >= 0xD800 && last <= 0xDBFF) out = out.slice(0, -1);
  return out;
}

// null when the payload has no list at all (older Claude Code, Codex): the app must not read that as "nothing runs".
function backgroundTasks(value) {
  if (!Array.isArray(value)) return null;
  const out = [];
  for (const task of value) {
    if (!task || typeof task !== 'object') continue;
    const id = task.id == null ? null : String(task.id);
    const type = str(task.type);
    if (!id || type === null || !TRACKED_TASK_TYPES.has(type)) continue;
    if (out.length >= MAX_BACKGROUND_TASKS) return null;
    out.push({ id, type, agent_type: str(task.agent_type), name: truncate(task.name) });
  }
  return out;
}

function buildLine(agent, payload) {
  if (agent !== 'claude' && agent !== 'codex') return null;
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return null;
  const event = str(payload.hook_event_name);
  const sessionId = payload.session_id == null ? null : String(payload.session_id);
  if (!event || !sessionId) return null;
  const isSubagentEvent = event === 'SubagentStart' || event === 'SubagentStop';
  if (payload.agent_id && !isSubagentEvent) return null; // fired inside a subagent's own context
  const message = payload.message ?? payload.last_assistant_message ?? payload.error ?? null;
  const host = (event === 'SessionStart' || event === 'UserPromptSubmit') ? {
    ppid: Number.isInteger(process.ppid) ? process.ppid : null,
    herdr_pane: str(process.env.HERDR_PANE_ID),
    wt_session: str(process.env.WT_SESSION),
    term_program: str(process.env.TERM_PROGRAM),
    vscode_pid: process.env.VSCODE_PID && /^\d+$/.test(process.env.VSCODE_PID) ? Number(process.env.VSCODE_PID) : null,
    wmux_pty: str(process.env.WMUX_PTY_ID),
    // Where the session was started: "cli", "claude-desktop", "claude-vscode", "sdk-ts"... (Claude Code only).
    entrypoint: str(process.env.CLAUDE_CODE_ENTRYPOINT),
  } : null;
  return JSON.stringify({
    ts: new Date().toISOString(),
    agent,
    event,
    session_id: sessionId,
    cwd: str(payload.cwd),
    notification_type: str(payload.notification_type),
    message: truncate(message),
    source: str(payload.source),
    agent_id: isSubagentEvent && payload.agent_id != null ? String(payload.agent_id) : null,
    agent_type: isSubagentEvent ? str(payload.agent_type) : null,
    transcript_path: str(payload.transcript_path),
    agent_transcript_path: event === 'SubagentStop' ? str(payload.agent_transcript_path) : null,
    background_tasks: (event === 'Stop' || event === 'SubagentStop') ? backgroundTasks(payload.background_tasks) : null,
    host,
  });
}

function main() {
  const agent = (process.argv[2] || '').toLowerCase();
  const eventsFile = process.env.AIUM_EVENTS_FILE || path.join(os.homedir(), '.aiusagemonitor', 'events.jsonl');
  let raw = '';
  try { raw = fs.readFileSync(0, 'utf8'); } catch { raw = ''; }
  let payload = null;
  try { payload = raw.trim() ? JSON.parse(raw) : null; } catch { payload = null; }
  const line = buildLine(agent, payload);
  if (line === null) return;
  try {
    fs.mkdirSync(path.dirname(eventsFile), { recursive: true });
    fs.appendFileSync(eventsFile, line + '\n', 'utf8');
  } catch {
    // never fail the agent because of the monitor
  }
}

if (require.main === module) {
  try { main(); } catch { /* swallow everything */ }
  process.exitCode = 0;
}

module.exports = { buildLine };
