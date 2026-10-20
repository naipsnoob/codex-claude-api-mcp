import { randomUUID } from "node:crypto";
import { mkdir, readFile, readdir, rename, unlink, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const activeStatuses = new Set(["queued", "running", "cancel_requested"]);
const taskStatuses = new Set([...activeStatuses, "succeeded", "failed", "rate_limited", "cancelled", "interrupted"]);
const rootDirectory = join(process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local"), "Codex", "mcp", "claude-external");

function taskPath(directory, id) {
  if (!/^[0-9a-f-]{36}$/i.test(id)) throw new Error("Invalid Claude task ID.");
  return join(directory, "tasks", `${id}.json`);
}

function cleanTask(value) {
  if (!value || !/^[0-9a-f-]{36}$/i.test(value.id) || !taskStatuses.has(value.status)) {
    throw new Error("Invalid Claude task record.");
  }
  const task = {
    id: value.id,
    status: value.status,
    created_at: String(value.created_at || new Date().toISOString()),
    updated_at: String(value.updated_at || new Date().toISOString()),
  };
  for (const key of ["parent_id", "working_directory", "started_at", "completed_at", "report", "error"]) {
    if (typeof value[key] === "string") task[key] = value[key].slice(0, key === "report" ? 1_000_000 : 20_000);
  }
  if (Number.isSafeInteger(value.owner_pid) && value.owner_pid > 0) task.owner_pid = value.owner_pid;
  task.actions = Array.isArray(value.actions) ? value.actions.filter((item) => typeof item === "string").slice(-500).map((item) => item.slice(0, 2_000)) : [];
  task.usage_calls = Array.isArray(value.usage_calls)
    ? value.usage_calls.filter((item) => Number.isSafeInteger(item?.input) && item.input >= 0 && Number.isSafeInteger(item?.output) && item.output >= 0).slice(-1000).map(({ input, output }) => ({ input, output }))
    : [];
  return task;
}

async function readJson(path, fallback) {
  try {
    return JSON.parse(await readFile(path, "utf8"));
  } catch (error) {
    if (error.code === "ENOENT") return fallback;
    throw error;
  }
}

async function writeJsonAtomic(path, value) {
  await mkdir(dirname(path), { recursive: true });
  const temporaryPath = `${path}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporaryPath, JSON.stringify(value, null, 2), "utf8");
    await rename(temporaryPath, path);
  } catch (error) {
    await unlink(temporaryPath).catch(() => {});
    throw error;
  }
}

export function createStateStore(directory = rootDirectory) {
  const tasksDirectory = join(directory, "tasks");
  const usagePath = join(directory, "usage.json");

  async function getTask(id) {
    const value = await readJson(taskPath(directory, id), null);
    return value ? cleanTask(value) : null;
  }

  async function createTask(value) {
    const task = cleanTask(value);
    const path = taskPath(directory, task.id);
    if (await readJson(path, null)) throw new Error("Claude task ID already exists.");
    await writeJsonAtomic(path, task);
    return task;
  }

  async function updateTask(id, patch) {
    const current = await getTask(id);
    if (!current) throw new Error("Claude task was not found.");
    const task = cleanTask({ ...current, ...patch, id, updated_at: new Date().toISOString() });
    if (await isCancellationRequested(id) && activeStatuses.has(task.status)) task.status = "cancel_requested";
    await writeJsonAtomic(taskPath(directory, id), task);
    return task;
  }

  async function isCancellationRequested(id) {
    try {
      await readFile(join(tasksDirectory, `${id}.cancel`), "utf8");
      return true;
    } catch (error) {
      if (error.code === "ENOENT") return false;
      throw error;
    }
  }

  async function requestCancellation(id) {
    const task = await getTask(id);
    if (!task) return { accepted: false, task: null };
    if (!activeStatuses.has(task.status)) return { accepted: false, task };
    await mkdir(tasksDirectory, { recursive: true });
    await writeFile(join(tasksDirectory, `${id}.cancel`), new Date().toISOString(), "utf8").catch((error) => {
      if (error.code !== "EEXIST") throw error;
    });
    return { accepted: true, task: await updateTask(id, { status: "cancel_requested" }) };
  }

  async function clearCancellation(id) {
    await unlink(join(tasksDirectory, `${id}.cancel`)).catch((error) => {
      if (error.code !== "ENOENT") throw error;
    });
  }

  async function recoverInterruptedTasks(now = new Date()) {
    await mkdir(tasksDirectory, { recursive: true });
    const names = await readdir(tasksDirectory);
    const recovered = [];
    for (const name of names.filter((item) => /^[0-9a-f-]{36}\.json$/i.test(item))) {
      const id = name.slice(0, -5);
      const task = await getTask(id);
      if (task && activeStatuses.has(task.status)) {
        if (task.owner_pid) {
          try {
            process.kill(task.owner_pid, 0);
            continue;
          } catch (error) {
            if (error.code !== "ESRCH") continue;
          }
        }
        const updated = await updateTask(id, {
          status: "interrupted",
          completed_at: now.toISOString(),
          owner_pid: null,
          error: "MCP restarted before the task completed; continue from this report to start a fresh Claude run.",
        });
        await clearCancellation(id);
        recovered.push(updated.id);
      }
    }
    return recovered;
  }

  async function recordUsage(usage, now = new Date()) {
    const input = Number.isSafeInteger(usage?.input) && usage.input >= 0 ? usage.input : 0;
    const output = Number.isSafeInteger(usage?.output) && usage.output >= 0 ? usage.output : 0;
    const ledger = await readJson(usagePath, { days: {} });
    const day = now.toISOString().slice(0, 10);
    const current = ledger.days?.[day] || { api_calls: 0, input_tokens: 0, output_tokens: 0 };
    ledger.days ??= {};
    ledger.days[day] = {
      api_calls: current.api_calls + 1,
      input_tokens: current.input_tokens + input,
      output_tokens: current.output_tokens + output,
    };
    await writeJsonAtomic(usagePath, ledger);
    return { input, output, day };
  }

  async function getUsageSummary(now = new Date()) {
    const ledger = await readJson(usagePath, { days: {} });
    const days = ledger.days || {};
    const totals = Object.values(days).reduce((sum, day) => ({
      api_calls: sum.api_calls + (Number.isSafeInteger(day.api_calls) ? day.api_calls : 0),
      input_tokens: sum.input_tokens + (Number.isSafeInteger(day.input_tokens) ? day.input_tokens : 0),
      output_tokens: sum.output_tokens + (Number.isSafeInteger(day.output_tokens) ? day.output_tokens : 0),
    }), { api_calls: 0, input_tokens: 0, output_tokens: 0 });
    const today = now.toISOString().slice(0, 10);
    return {
      today_utc: today,
      today: days[today] || { api_calls: 0, input_tokens: 0, output_tokens: 0 },
      totals,
      gateway_remaining_quota: "unavailable from the Messages API",
    };
  }

  return { directory, createTask, getTask, updateTask, isCancellationRequested, requestCancellation, clearCancellation, recoverInterruptedTasks, recordUsage, getUsageSummary };
}
