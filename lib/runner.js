import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { copyFile, mkdir, mkdtemp, readFile, readdir, rename, rm, stat, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const DRIVER_SOURCE = join(HERE, "..", "driver", "Program.cs");
const INTEROP_MAIN = "SolidWorks.Interop.sldworks.dll";
const INTEROP_CONST = "SolidWorks.Interop.swconst.dll";

function frameworkDir() {
  const windir = process.env.SystemRoot ?? process.env.windir ?? "C:\\Windows";
  for (const arch of ["Framework64", "Framework"]) {
    const dir = join(windir, "Microsoft.NET", arch, "v4.0.30319");
    if (existsSync(join(dir, "csc.exe"))) return dir;
  }
  throw new Error(
    "The .NET Framework 4 C# compiler (csc.exe) was not found. It ships with Windows 10/11; enable \".NET Framework 4.8\" in Windows Features.",
  );
}

function cacheRoot() {
  return join(process.env.LOCALAPPDATA ?? process.env.USERPROFILE ?? "C:\\", "dsh-solidworks");
}

function runProcess(command, args, { timeoutMs, signal } = {}) {
  return new Promise((resolve) => {
    const child = spawn(command, args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    let settled = false;
    let timer;
    let abortListener;

    const finish = (code, extra) => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      if (abortListener) signal?.removeEventListener("abort", abortListener);
      resolve({ code, stdout, stderr, ...extra });
    };

    child.stdout?.setEncoding("utf8");
    child.stderr?.setEncoding("utf8");
    child.stdout?.on("data", (chunk) => { stdout += chunk; });
    child.stderr?.on("data", (chunk) => { stderr += chunk; });
    child.on("error", (error) => finish(-1, { spawnError: error }));
    child.on("close", (code) => finish(code ?? -1, {}));

    if (timeoutMs && timeoutMs > 0) {
      timer = setTimeout(() => {
        child.kill();
        finish(-2, { timedOut: true });
      }, timeoutMs);
      timer.unref?.();
    }
    if (signal) {
      abortListener = () => {
        child.kill();
        finish(-3, { aborted: true });
      };
      if (signal.aborted) abortListener();
      else signal.addEventListener("abort", abortListener, { once: true });
    }
  });
}

function registryValue(stdout) {
  const match = /REG_SZ\s+(.+?)\s*$/m.exec(stdout);
  return match?.[1]?.trim() ?? null;
}

async function registryQuery(args) {
  const result = await runProcess("reg.exe", args, { timeoutMs: 15000 });
  if (result.code !== 0) return null;
  return registryValue(result.stdout);
}

/** The folder holding the SolidWorks interop assemblies, used to compile the driver. */
export async function findInteropDir({ interopDir, exePath }) {
  const candidates = [];
  if (interopDir) candidates.push(interopDir);
  const executables = [];
  if (exePath) executables.push(exePath);
  try {
    const clsid = await registryQuery(["query", "HKCR\\SldWorks.Application\\CLSID", "/ve"]);
    if (clsid) {
      const server = await registryQuery(["query", `HKCR\\CLSID\\${clsid}\\LocalServer32`, "/ve"]);
      if (server) executables.push(server.replace(/^"|"$/g, ""));
    }
  } catch {}
  for (const exe of executables) {
    if (typeof exe !== "string" || exe.length === 0) continue;
    candidates.push(join(dirname(exe), "api", "redist"));
  }
  const roots = [
    process.env.ProgramFiles,
    process.env["ProgramW6432"],
    "C:\\Program Files",
    "D:\\Program Files",
    "E:\\Program Files",
    "F:\\Program Files",
  ].filter(Boolean);
  for (const root of roots) {
    for (const vendor of ["SOLIDWORKS Corp\\SOLIDWORKS", "SolidWorks Corp\\SolidWorks"]) {
      candidates.push(join(root, vendor, "api", "redist"));
    }
  }
  for (const dir of candidates) {
    if (dir && existsSync(join(dir, INTEROP_MAIN))) return dir;
  }
  throw new Error(
    "SolidWorks was not found on this computer: neither the SldWorks.Application COM registration nor the interop assemblies (api\\redist) could be located. Install SolidWorks, or set interopDir in the plugin config.",
  );
}

/** Compiles the driver once per source/interop version and caches the exe. */
async function buildDriver(config) {
  const cscDir = frameworkDir();
  const interop = await findInteropDir(config);
  const source = await readFile(DRIVER_SOURCE, "utf8");
  const hash = createHash("sha256")
    .update(source)
    .update("|")
    .update(interop)
    .update("|")
    .update(cscDir)
    .digest("hex")
    .slice(0, 12);

  const dir = join(cacheRoot(), `driver-${hash}`);
  const exe = join(dir, "dsh-solidworks-driver.exe");
  const dllMain = join(dir, INTEROP_MAIN);
  const dllConst = join(dir, INTEROP_CONST);

  void pruneOldBuilds(dir);

  if (existsSync(exe) && existsSync(dllMain) && existsSync(dllConst)) return exe;

  await mkdir(dir, { recursive: true });
  await copyFile(join(interop, INTEROP_MAIN), dllMain);
  await copyFile(join(interop, INTEROP_CONST), dllConst);

  const temp = join(dir, `build-${process.pid}-${Date.now()}.exe`);
  const result = await runProcess(
    join(cscDir, "csc.exe"),
    [
      "/nologo",
      "/target:exe",
      "/platform:x64",
      "/optimize+",
      `/out:${temp}`,
      `/r:${join(interop, INTEROP_MAIN)}`,
      `/r:${join(interop, INTEROP_CONST)}`,
      "/r:System.Web.Extensions.dll",
      "/r:System.Drawing.dll",
      DRIVER_SOURCE,
    ],
    { timeoutMs: 180000 },
  );

  if (result.code !== 0 || !existsSync(temp)) {
    const detail = (result.stderr || result.stdout || "").trim();
    throw new Error(
      "Could not compile the SolidWorks automation helper with csc.exe." +
        (detail ? `\n${detail}` : "") +
        (result.timedOut ? "\nThe compiler timed out." : ""),
    );
  }

  await rm(exe, { force: true });
  await rename(temp, exe);
  return exe;
}

/** Removes driver builds from earlier source versions once they are a day old.
 *  Recent ones are kept: another session may still be using them. */
async function pruneOldBuilds(keep) {
  try {
    const root = cacheRoot();
    const entries = await readdir(root, { withFileTypes: true });
    const cutoff = Date.now() - 24 * 60 * 60 * 1000;
    for (const entry of entries) {
      if (!entry.isDirectory() || !entry.name.startsWith("driver-")) continue;
      const full = join(root, entry.name);
      if (full === keep) continue;
      try {
        const info = await stat(full);
        if (info.mtimeMs >= cutoff) continue;
      } catch {
        continue;
      }
      await rm(full, { recursive: true, force: true }).catch(() => {});
    }
  } catch {
    // A cache that cannot be pruned is not worth failing a call over.
  }
}

export class SolidWorksDriver {
  #config;
  #building;
  #queue = Promise.resolve();

  constructor(config) {
    this.#config = config;
  }

  #ensure() {
    if (!this.#building) {
      this.#building = buildDriver(this.#config).catch((error) => {
        this.#building = undefined;
        throw error;
      });
    }
    return this.#building;
  }

  /** Runs one driver request. Requests are serialized: SolidWorks automation is single-threaded. */
  run(request, options = {}) {
    const task = this.#queue.then(() => this.#runOnce(request, options));
    this.#queue = task.catch(() => {});
    return task;
  }

  async #runOnce(request, { signal } = {}) {
    let exe = await this.#ensure();
    if (!existsSync(exe)) {
      // The cached build was removed between calls (cleared cache); compile it again.
      this.#building = undefined;
      exe = await this.#ensure();
    }
    const dir = await mkdtemp(join(cacheRoot(), "run-"));
    try {
      const requestPath = join(dir, "request.json");
      const responsePath = join(dir, "response.json");
      await writeFile(requestPath, JSON.stringify(request), "utf8");
      const run = await runProcess(exe, [requestPath, responsePath], {
        timeoutMs: Math.max(30, this.#config.timeoutSec || 180) * 1000 + 30000,
        signal,
      });
      if (run.aborted) throw new Error("The SolidWorks operation was cancelled.");
      if (!existsSync(responsePath)) {
        const detail = (run.stderr || "").trim();
        throw new Error(
          "The SolidWorks helper stopped without an answer" +
            (run.timedOut ? " (timed out; SolidWorks may be showing a dialog)" : "") +
            (detail ? `:\n${detail}` : "."),
        );
      }
      const text = await readFile(responsePath, "utf8");
      let parsed;
      try {
        parsed = JSON.parse(text.replace(/^\uFEFF/, ""));
      } catch {
        throw new Error("The SolidWorks helper returned unreadable output.");
      }
      if (!parsed?.ok) {
        const error = new Error(parsed?.error?.message ?? "SolidWorks automation failed.");
        error.kind = parsed?.error?.kind;
        throw error;
      }
      return parsed.data;
    } finally {
      await rm(dir, { recursive: true, force: true }).catch(() => {});
    }
  }
}


