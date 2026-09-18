// Opt-in real App Server acceptance. Uses the installed Codex account/model.
// Usage: node scripts/verify-codex-task.mjs <shim.exe> <project-root> [--model-turn]
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';
import { resolve, join } from 'node:path';
import { readFile, rm, access } from 'node:fs/promises';
import assert from 'node:assert/strict';

const [shim, directory, mode] = process.argv.slice(2);
assert(shim && directory, 'Provide shim.exe and an existing project root');
const root = resolve(directory);
const marker = join(root, `.projectbridge-verify-${randomUUID()}.txt`);
const child = spawn(resolve(shim), ['app-server'], {
  cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, LPB_PROJECT_ROOT: root, LPB_CODEX_ALLOW_WRITE: '1' },
});
const pending = new Map();
let nextId = 0;
let threadId;
let stderr = '';
let finishTurn;
const terminal = new Promise(resolve => { finishTurn = resolve; });
child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-8000); });
createInterface({ input: child.stdout }).on('line', line => {
  const message = JSON.parse(line);
  if (message.id && message.method) {
    child.stdin.write(JSON.stringify({ id: message.id, error: { code: -32601, message: 'No interactive approvals in acceptance test' } }) + '\n');
  } else if (message.id) {
    pending.get(message.id)?.(message);
  } else if (message.method === 'turn/completed') {
    finishTurn(message.params.turn);
  } else if (message.method === 'item/completed') {
    const item = message.params.item;
    if (['agentMessage', 'commandExecution', 'fileChange'].includes(item?.type)) console.log(JSON.stringify(item));
  }
});
async function request(method, params) {
  const id = ++nextId;
  const response = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timeout: ${method}\n${stderr}`)); }, 45000);
    pending.set(id, result => { clearTimeout(timer); pending.delete(id); resolve(result); });
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
  });
  assert(!response.error, JSON.stringify(response.error));
  return response.result;
}
try {
  await request('initialize', { clientInfo: { name: 'projectbridge-acceptance', version: '1' } });
  child.stdin.write('{"method":"initialized"}\n');
  const start = await request('thread/start', { ephemeral: mode !== '--model-turn', persistExtendedHistory: true });
  threadId = start.thread.id;
  console.log(JSON.stringify({ threadId, cwd: start.cwd, sandbox: start.sandbox, path: start.thread.path }));
  const escaped = marker.replaceAll("'", "''");
  const write = await request('command/exec', {
    command: ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', `$ErrorActionPreference='Stop'; Set-Content -LiteralPath '${escaped}' -Value 'preflight' -NoNewline`], timeoutMs: 10000,
  });
  assert.equal(write.exitCode, 0, JSON.stringify(write));
  assert.equal(await readFile(marker, 'utf8'), 'preflight');
  if (mode === '--model-turn') {
    await request('thread/name/set', { threadId, name: 'ProjectBridge acceptance — ChatGPT write task' });
    await request('turn/start', { threadId, input: [{ type: 'text', text:
      `|from_chatgpt|:\nProjectBridge release acceptance. In the authorized project ${root}, overwrite ONLY ${marker} with one UTF-8 line MODEL_WRITE_OK (a trailing newline is allowed), use your file-edit tool, then read it back. Do not edit any other file. Report actual tool failures. This is a small test; do not read unrelated project files or run other work.` }] });
    let timer;
    const turn = await Promise.race([terminal, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Model turn timed out')), 240000); })]).finally(() => clearTimeout(timer));
    assert.equal(turn.status, 'completed', JSON.stringify(turn));
    assert.match(await readFile(marker, 'utf8'), /^MODEL_WRITE_OK(?:\r?\n)?$/);
    const stored = await request('thread/read', { threadId, includeTurns: true });
    assert.equal(stored.thread.ephemeral, false);
    assert(JSON.stringify(stored.thread.turns).includes('|from_chatgpt|:'));
    await access(stored.thread.path);
    console.log(JSON.stringify({ verified: true, threadId, path: stored.thread.path, modelWrite: true }));
  } else console.log(JSON.stringify({ verified: true, commandWrite: true }));
} finally {
  child.stdin.end();
  const timer = setTimeout(() => child.kill(), 3000);
  await new Promise(resolve => { if (child.exitCode !== null) resolve(); else child.once('exit', resolve); });
  clearTimeout(timer);
  await rm(marker, { force: true });
}
