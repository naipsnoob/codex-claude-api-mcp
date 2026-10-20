import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildMessageRequest } from "./request.mjs";
import { createClaudeQueue, formatRateLimitError, localToolSelfcheck, redactText, runClaudeTask, runClaudeVerification } from "./executor.mjs";
import { createStateStore } from "./state-store.mjs";

const request = buildMessageRequest("review this code");
assert.equal(request.model, "claude-opus-5");
assert.equal(request.max_tokens, 128000);
assert.equal(request.output_config.effort, "high");
assert.equal("thinking" in request, false);
assert.deepEqual(request.messages, [{ role: "user", content: "review this code" }]);
console.log("Claude Opus 5 request configuration is valid.");

const rateLimitError = formatRateLimitError({ status: 429, headers: new Headers({
  "retry-after": "12",
  "anthropic-ratelimit-requests-remaining": "0",
  "anthropic-ratelimit-requests-reset": "2026-10-19T15:00:00Z",
  "authorization": "secret-must-not-appear",
}) }, new Date("2026-10-19T14:59:48Z"));
assert.match(rateLimitError, /2026-10-19T14:59:48\.000Z/);
assert.match(rateLimitError, /retry-after: 12/);
assert.match(rateLimitError, /anthropic-ratelimit-requests-reset: 2026-10-19T15:00:00Z/);
assert.doesNotMatch(rateLimitError, /secret|injected|authorization/);
assert.doesNotMatch(formatRateLimitError({ status: 429, headers: {
  "anthropic-ratelimit-tokens-remaining": "secret\ninjected",
} }), /secret|injected/);
assert.equal(redactText("api_key: test-secret-value"), "api_key: [REDACTED]");
console.log("HTTP 429 diagnostic formatter is valid.");

const tempDir = await mkdtemp(join(tmpdir(), "codex-claude-selfcheck-"));
try {
  const stateStore = createStateStore(join(tempDir, "state"));
  const persistedTask = await stateStore.createTask({
    id: "01234567-89ab-4cde-8fab-0123456789ab",
    status: "running",
    created_at: "2026-10-19T12:00:00.000Z",
    working_directory: tempDir,
    report: "Confirmed report",
    usage_calls: [{ input: 3, output: 4 }],
    prompt: "must-not-be-persisted",
    tool_result: "private tool output must not be persisted",
  });
  assert.equal(persistedTask.status, "running");
  const persistedJson = await readFile(join(tempDir, "state", "tasks", `${persistedTask.id}.json`), "utf8");
  assert.doesNotMatch(persistedJson, /must-not-be-persisted|private tool output/);
  await stateStore.recordUsage({ input: 3, output: 4 }, new Date("2026-10-19T12:01:00.000Z"));
  assert.deepEqual((await stateStore.getUsageSummary(new Date("2026-10-19T12:02:00.000Z"))).today, { api_calls: 1, input_tokens: 3, output_tokens: 4 });
  const activeTask = await stateStore.createTask({ id: "11234567-89ab-4cde-8fab-0123456789ab", status: "running", owner_pid: process.pid });
  assert.equal((await stateStore.requestCancellation(persistedTask.id)).accepted, true);
  assert.equal(await stateStore.isCancellationRequested(persistedTask.id), true);
  await stateStore.recoverInterruptedTasks(new Date("2026-10-19T12:03:00.000Z"));
  assert.equal((await stateStore.getTask(persistedTask.id)).status, "interrupted");
  assert.equal((await stateStore.getTask(activeTask.id)).status, "running", "Another live MCP process must not be marked interrupted during startup recovery.");
  console.log("Task metadata persistence, secret-free storage, cancellation, recovery, and daily usage ledger passed.");

  let cancelledCalls = 0;
  const cancelledTask = await runClaudeTask({ messages: { async create() { cancelledCalls++; } } }, "cancel before starting", tempDir, 2, 0, { shouldCancel: () => true });
  assert.equal(cancelledTask.cancelled, true);
  assert.equal(cancelledCalls, 0);
  const cancelledQueue = createClaudeQueue(join(tempDir, "cancel.lock"));
  let queuedCallbackRan = false;
  await assert.rejects(cancelledQueue(async () => { queuedCallbackRan = true; }, () => true), /cancellation requested/i);
  assert.equal(queuedCallbackRan, false);

  await writeFile(join(tempDir, "marker.txt"), "executor-selfcheck", "utf8");
  let deniedWriteCalls = 0;
  const deniedWrite = await runClaudeVerification({ messages: { async create() {
    deniedWriteCalls++;
    return { content: [{ type: "tool_use", id: "verify-deny", name: "write_file", input: { path: "must-not-exist.txt", content: "blocked" } }], usage: { input_tokens: 1, output_tokens: 1 } };
  } } }, "Check this evidence", tempDir, { messageGapMs: 0 });
  assert.equal(deniedWrite.verdict, "insufficient_evidence");
  assert.equal(deniedWriteCalls, 1);
  await assert.rejects(readFile(join(tempDir, "must-not-exist.txt"), "utf8"), { code: "ENOENT" });

  let verificationCalls = 0;
  const verified = await runClaudeVerification({ messages: { async create(request) {
    verificationCalls++;
    if (verificationCalls === 1) return { content: [{ type: "tool_use", id: "verify-read", name: "read_file", input: { path: "marker.txt" } }], usage: { input_tokens: 1, output_tokens: 1 } };
    assert.ok(request.messages.at(-1).content.some((block) => block.type === "tool_result" && block.content === "executor-selfcheck"));
    return { content: [{ type: "text", text: JSON.stringify({ verdict: "pass", summary: "Marker verified", findings: [], evidence: ["marker.txt contained executor-selfcheck"] }) }], usage: { input_tokens: 2, output_tokens: 3 } };
  } } }, "Verify the marker", tempDir, { messageGapMs: 0 });
  assert.equal(verified.verdict, "pass");
  assert.deepEqual(verified.usageCalls, [{ input: 1, output: 1 }, { input: 2, output: 3 }]);

  let rateLimitCalls = 0;
  const rateLimited = await runClaudeTask({ messages: { async create() {
    rateLimitCalls++;
    const error = new Error("429");
    error.status = 429;
    error.headers = new Headers({ "retry-after": "5" });
    throw error;
  } } }, "must not retry", tempDir, 12, 0);
  assert.equal(rateLimited.rateLimited, true);
  assert.equal(rateLimitCalls, 1, "A 429 must not trigger an automatic retry.");
  console.log("Cooperative cancellation, read-only verification, evidence checks, and no-retry-on-429 passed.");

  const queueA = createClaudeQueue(join(tempDir, "api.lock"));
  const queueB = createClaudeQueue(join(tempDir, "api.lock"));
  let active = 0;
  let maxActive = 0;
  const taskStarts = [];
  const queuedTask = (label) => (async () => {
    taskStarts.push(performance.now());
    active++;
    maxActive = Math.max(maxActive, active);
    await new Promise((done) => setTimeout(done, 20));
    active--;
    return label;
  })();
  const [first, second] = await Promise.all([queueA(() => queuedTask("first")), queueB(() => queuedTask("second"))]);
  assert.deepEqual([first, second].sort(), ["first", "second"]);
  assert.equal(maxActive, 1, "Claude API tasks must serialize across queue instances.");
  assert.ok(taskStarts[1] - taskStarts[0] >= 5_000, "Separate Claude API tasks must have a five-second gap.");
  const execution = await localToolSelfcheck(tempDir);
  assert.equal(execution.ok, true);
  assert.match(execution.usage, /API calls=5/);
  const calls = [];
  const paced = await runClaudeTask({ messages: { async create(request) {
    calls.push(performance.now());
    if (calls.length === 1) return { content: [{ type: "tool_use", id: "pace", name: "list_directory", input: { path: "." } }], usage: {} };
    assert.equal(request.messages.at(-1).content[0].type, "tool_result");
    return { content: [{ type: "text", text: "pace verified" }], usage: {} };
  } } }, "List the temporary directory", tempDir, 2, 60);
  assert.equal(paced.ok, true);
  assert.ok(calls[1] - calls[0] >= 60, "Consecutive Messages calls must be at least 60 ms apart in this selfcheck.");
  console.log("Cross-queue serialization, tool_use loop, absolute paths, PowerShell/CMD, and Messages pacing passed.");

  const serverEnv = Object.fromEntries(Object.entries({
    PATH: process.env.PATH,
    SystemRoot: process.env.SystemRoot,
    LOCALAPPDATA: join(tempDir, "mcp-server-state"),
    TEMP: process.env.TEMP,
    TMP: process.env.TMP,
  }).filter(([, value]) => typeof value === "string"));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("./server.mjs", import.meta.url))],
    cwd: process.cwd(),
    env: serverEnv,
  });
  const mcpClient = new Client({ name: "claude-external-selfcheck", version: "1.0.0" });
  try {
    await mcpClient.connect(transport);
    const tools = await mcpClient.listTools();
    const names = tools.tools.map((tool) => tool.name);
    for (const name of ["ask_claude_opus_5", "execute_with_claude_opus_5", "start_claude_task", "wait_claude_task", "get_claude_task_result", "continue_claude_task", "cancel_claude_task", "verify_claude_task", "get_claude_usage"]) {
      assert.ok(names.includes(name), `MCP tool ${name} must be registered.`);
    }
    const usageResult = await mcpClient.callTool({ name: "get_claude_usage", arguments: {} });
    const usageText = usageResult.content.find((item) => item.type === "text")?.text;
    const usageSummary = JSON.parse(usageText);
    assert.equal(usageSummary.today.api_calls, 0);
    assert.match(usageSummary.gateway_remaining_quota, /unavailable/);
    console.log("MCP stdio startup, task tool registration, and usage query passed.");
  } finally {
    await mcpClient.close();
  }
} finally {
  await rm(tempDir, { recursive: true, force: true });
}
