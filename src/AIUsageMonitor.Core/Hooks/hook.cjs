#!/usr/bin/env node
'use strict';
// AIUsageMonitor hook bridge.
// Usage: node hook.cjs claude|codex   (agent hook payload on stdin)
// Appends one JSON line to ~/.aiusagemonitor/events.jsonl (or $AIUM_EVENTS_FILE) and ALWAYS exits 0.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const MAX_MESSAGE = 200;

function str(value) {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function truncate(value) {
  const s = str(value);
  return s === null ? null : s.length > MAX_MESSAGE ? s.slice(0, MAX_MESSAGE) : s;
}

function buildLine(agent, payload) {
  if (agent !== 'claude' && agent !== 'codex') return null;
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return null;
  if (payload.agent_id) return null; // Claude subagent context: not a user-facing session
  const event = str(payload.hook_event_name);
  const sessionId = payload.session_id == null ? null : String(payload.session_id);
  if (!event || !sessionId) return null;
  const message = payload.message ?? payload.last_assistant_message ?? payload.error ?? null;
  return JSON.stringify({
    ts: new Date().toISOString(),
    agent,
    event,
    session_id: sessionId,
    cwd: str(payload.cwd),
    notification_type: str(payload.notification_type),
    message: truncate(message),
    source: str(payload.source),
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
