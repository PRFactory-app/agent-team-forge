import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import activate from "../index";

const previous = process.env.ATF_STATE_DIR;
afterEach(() => {
  if (previous === undefined) delete process.env.ATF_STATE_DIR;
  else process.env.ATF_STATE_DIR = previous;
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

  it("injects a committed notice into its own Pi session", async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), "atf-pi-"));
    process.env.ATF_STATE_DIR = dir;
    const spool = path.join(dir, `pi-wake-${process.pid}.jsonl`);
    await writeFile(spool, JSON.stringify({ generation: 1, notice: "call job_get" }) + "\n");
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
      await vi.waitFor(() => expect(sends).toEqual(["call job_get"]));
    } finally {
      await handlers.session_shutdown();
      await rm(dir, { recursive: true, force: true });
    }
  });
});
