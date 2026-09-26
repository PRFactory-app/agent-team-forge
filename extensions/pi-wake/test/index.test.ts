import { appendFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import activate from "../index";

const previous = process.env.ATF_STATE_DIR;
const previousHome = process.env.HOME;
afterEach(() => {
  if (previous === undefined) delete process.env.ATF_STATE_DIR;
  else process.env.ATF_STATE_DIR = previous;
  if (previousHome === undefined) delete process.env.HOME;
  else process.env.HOME = previousHome;
});

describe("AgentTeamForge Pi adapter", () => {
  it("does not activate outside an AgentTeamForge state directory", () => {
    delete process.env.ATF_STATE_DIR;
    const handlers: Record<string, () => Promise<void> | void> = {};
    activate({
      on: (name: string, handler: () => Promise<void> | void) => {
        handlers[name] = handler;
      },
    } as never);
    expect(handlers.session_start).toBeUndefined();
  });

  it("injects new notices into its own Pi session and skips earlier ones", async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), "atf-pi-"));
    process.env.ATF_STATE_DIR = dir;
    const spool = path.join(dir, `pi-wake-${process.pid}.jsonl`);
    await writeFile(
      spool,
      JSON.stringify({ generation: 1, notice: "stale from an earlier pid owner" }) + "\n",
    );
    const handlers: Record<string, () => Promise<void> | void> = {};
    const sends: string[] = [];
    activate({
      on: (name: string, handler: () => Promise<void> | void) => {
        handlers[name] = handler;
      },
      sendMessage: (message: { content: string }) => {
        sends.push(message.content);
      },
    } as never);
    try {
      await handlers.session_start();
      await appendFile(spool, JSON.stringify({ generation: 2, notice: "call job_get" }) + "\n");
      await vi.waitFor(() => expect(sends).toEqual(["call job_get"]), { timeout: 3000 });
    } finally {
      await handlers.session_shutdown();
      await rm(dir, { recursive: true, force: true });
    }
  });

  it("reads the state directory saved by setup", async () => {
    const home = await mkdtemp(path.join(os.tmpdir(), "atf-pi-home-"));
    const dir = path.join(home, "state");
    await mkdir(path.join(home, ".pi", "agent"), { recursive: true });
    await mkdir(dir);
    await writeFile(path.join(home, ".pi", "agent", "agentteamforge.json"), JSON.stringify({ stateDir: dir }));
    process.env.HOME = home;
    delete process.env.ATF_STATE_DIR;
    const handlers: Record<string, () => Promise<void> | void> = {};
    const sends: string[] = [];
    activate({
      on: (name: string, handler: () => Promise<void> | void) => { handlers[name] = handler; },
      sendMessage: (message: { content: string }) => { sends.push(message.content); },
    } as never);
    try {
      await handlers.session_start();
      await appendFile(path.join(dir, `pi-wake-${process.pid}.jsonl`), JSON.stringify({ notice: "wake" }) + "\n");
      await vi.waitFor(() => expect(sends).toEqual(["wake"]), { timeout: 3000 });
    } finally {
      await handlers.session_shutdown();
      await rm(home, { recursive: true, force: true });
    }
  });

  it("writes running and waiting state from Pi events", async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), "atf-pi-state-"));
    process.env.ATF_STATE_DIR = dir;
    const handlers: Record<string, () => Promise<void> | void> = {};
    activate({
      on: (name: string, handler: () => Promise<void> | void) => { handlers[name] = handler; },
    } as never);
    const marker = path.join(dir, `pi-state-${process.pid}.json`);
    try {
      handlers.turn_start();
      expect(JSON.parse(await readFile(marker, "utf8")).state).toBe("running");
      handlers.agent_settled();
      expect(JSON.parse(await readFile(marker, "utf8")).state).toBe("waiting");
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
