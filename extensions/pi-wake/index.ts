/**
 * AgentTeamForge adaptation of win-agent-teams-wake. The daemon commits a
 * notice-only JSONL doorbell; this Pi session injects it through sendMessage.
 * The lifecycle and injection API follow the reference extension.
 */
import { statSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { createLifecycle } from "./src/lifecycle";
import { sleep } from "./src/util";

export { createLifecycle } from "./src/lifecycle";
export { WakeMachine } from "./src/state-machine";

export default function activate(pi: ExtensionAPI): void {
  const stateDir = process.env.ATF_STATE_DIR;
  if (!stateDir) return;
  const spool = path.join(stateDir, `pi-wake-${process.pid}.jsonl`);
  let offset = 0;
  const lifecycle = createLifecycle({
    createController: () => new AbortController(),
    startLoop: async (signal) => {
      // Skip doorbells left by an earlier process with this PID; bridge
      // re-registration makes the daemon post a fresh catch-up notice.
      offset = statSync(spool, { throwIfNoEntry: false })?.size ?? 0;
      while (!signal.aborted) {
        try {
          const bytes = await readFile(spool);
          if (bytes.length < offset) offset = 0;
          const tail = bytes.subarray(offset).toString("utf8");
          const complete = tail.lastIndexOf("\n");
          if (complete >= 0) {
            offset += Buffer.byteLength(tail.slice(0, complete + 1));
            const notices = tail
              .slice(0, complete)
              .split("\n")
              .filter(Boolean)
              .flatMap((line) => {
                try {
                  const parsed: unknown = JSON.parse(line);
                  if (
                    typeof parsed === "object" &&
                    parsed !== null &&
                    typeof (parsed as { notice?: unknown }).notice === "string"
                  ) {
                    return [(parsed as { notice: string }).notice];
                  }
                } catch {
                  /* Ignore malformed or partial doorbells. */
                }
                return [];
              });
            if (notices.length > 0) {
              pi.sendMessage(
                { customType: "agent-team-forge/wake", content: notices.at(-1)!, display: true },
                { triggerTurn: true, deliverAs: "steer" },
              );
            }
          }
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
        }
        await sleep(1000, signal);
      }
    },
  });
  pi.on("session_start", async () => {
    await lifecycle.start();
  });
  pi.on("session_shutdown", async () => {
    await lifecycle.shutdown();
  });
}
