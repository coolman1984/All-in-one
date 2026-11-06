"""The separate watcher. It starts each stack, drives it through the same steps, and measures from outside.

Memory is the sum over the whole process tree (msedgewebview2 children included), sampled every 250 ms:
  private = private committed bytes: this is what counts against Windows commit, the owner's limit.
  ws      = working set: resident memory; shared pages are counted once per process, so it overstates.
Start time runs from just before the process is created until the stack reports its first drawn screen.

Usage: python watch.py compare <runs> | lazy <runs> | smoke <stack>
Stops at 96% Windows commit. Prints and saves only timings and sizes. Made-up data only.
"""
import ctypes
import http.server
import json
import os
import pathlib
import queue
import shutil
import statistics
import subprocess
import sys
import threading
import time

import psutil

ROOT = pathlib.Path(__file__).resolve().parent.parent
TMP = ROOT / ".tmp"
RESULTS = ROOT / "results"
NO_WINDOW = 0x08000000
STOP_AT = 96.0
SETTLE = 5.0


class MemStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong)] + [
        (n, ctypes.c_ulonglong) for n in ("tp", "ap", "tf", "af", "tv", "av", "ae")]


def commit_pct():
    m = MemStatus()
    m.dwLength = ctypes.sizeof(m)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return (1 - m.af / m.tf) * 100


class Tree:
    """Samples one process and all its children until stopped."""

    def __init__(self, pid):
        self.root = psutil.Process(pid)
        self.known = {pid: self.root}
        self.now = (0, 0, 0)
        self.recent = []
        self.peak = 0
        self.stop = False
        self.limit_hit = False
        threading.Thread(target=self.loop, daemon=True).start()

    def sample(self):
        try:
            for c in self.root.children(recursive=True):
                self.known.setdefault(c.pid, c)
        except psutil.Error:
            pass
        priv = ws = n = 0
        for pid, p in list(self.known.items()):
            try:
                m = p.memory_info()
                priv += m.private
                ws += m.rss
                n += 1
            except psutil.Error:
                del self.known[pid]
        return priv, ws, n

    def loop(self):
        while not self.stop:
            s = self.sample()
            self.now = s
            self.peak = max(self.peak, s[0])
            self.recent = (self.recent + [s])[-4:]
            if commit_pct() >= STOP_AT:
                self.limit_hit = True
            time.sleep(0.25)

    def steady(self):
        """Mean of the last second, in MB."""
        r = self.recent or [self.now]
        return {"private_mb": round(statistics.mean(x[0] for x in r) / 1048576, 1),
                "ws_mb": round(statistics.mean(x[1] for x in r) / 1048576, 1),
                "procs": r[-1][2]}


class App:
    """A stack driven through stdin and stdout."""

    def __init__(self, cmd, cwd=None):
        self.t0 = time.perf_counter()
        self.p = subprocess.Popen(cmd, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  encoding="utf-8", errors="replace", bufsize=1, creationflags=NO_WINDOW)
        self.q = queue.Queue()
        self.tree = Tree(self.p.pid)
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.p.stdout:
            self.q.put((time.perf_counter(), line.strip()))
        self.q.put((time.perf_counter(), "EOF"))

    def wait(self, word, timeout=120):
        end = time.time() + timeout
        while True:
            if self.tree.limit_hit:
                raise RuntimeError("stopped: Windows commit reached 96%")
            try:
                t, line = self.q.get(timeout=max(0.1, end - time.time()))
            except queue.Empty:
                raise RuntimeError(f"timed out waiting for {word}")
            if line.startswith(word):
                return t, line
            if line.startswith("ERROR") or line == "EOF":
                raise RuntimeError(f"waiting for {word}: {line}")

    def send(self, s):
        self.p.stdin.write(s + "\n")
        self.p.stdin.flush()

    def quit(self):
        try:
            self.send("quit")
        except OSError:
            pass
        close_tree(self.p, self.tree)


def close_tree(p, tree):
    """Closes what this watcher started, and only that."""
    t = time.time()
    try:
        p.wait(15)
    except subprocess.TimeoutExpired:
        p.kill()
    for q in list(tree.known.values()):  # children get a moment to leave by themselves
        try:
            if q.pid != p.pid:
                q.wait(max(0.1, t + 8 - time.time()))
        except psutil.Error:
            pass
    left = []
    for q in list(tree.known.values()):
        try:
            if q.is_running() and q.pid != p.pid:
                left.append(q)
                q.kill()
        except psutil.Error:
            pass
    tree.stop = True
    return len(left)


def fields(line):
    return {k: float(v) for k, v in (x.split("=") for x in line.split()[1:])}


def stacks():
    b = "bin/Release/net8.0-windows"
    return {
        "winforms-net": lambda run: [str(ROOT / "winforms" / b / "exp-winforms.exe")],
        "winforms-drawn": lambda run: [str(ROOT / "winforms" / b / "exp-winforms.exe"), "--canvas"],
        "winforms-net48": lambda run: [str(ROOT / "winforms/bin/net48/exp-winforms48.exe")],
        "wpf-net": lambda run: [str(ROOT / "wpf" / b / "exp-wpf.exe")],
        "webview2": lambda run: [str(ROOT / "webview2" / b / "win-x64/exp-webview2.exe"), "--udf", str(TMP / "udf-wv2")],
        "webview2-lean": lambda run: [str(ROOT / "webview2" / b / "win-x64/exp-webview2.exe"), "--lean", "--udf", str(TMP / "udf-wv2-lean")],
        "python-qt": lambda run: [sys.executable, str(ROOT / "python-qt/app.py")],
    }


def one(name, cmd, run):
    a = App(cmd)
    r = {"stack": name, "run": run}
    try:
        t, _ = a.wait("READY")
        r["first_screen_ms"] = round((t - a.t0) * 1000)
        time.sleep(SETTLE)
        r["idle"] = a.tree.steady()
        a.send("table")
        _, line = a.wait("TABLE_READY")
        r["table_ready_ms"] = float(line.split()[1])
        a.send("bench")
        _, line = a.wait("BENCH", 300)
        r.update(fields(line))
        time.sleep(SETTLE)
        r["table"] = a.tree.steady()
        r["peak_private_mb"] = round(a.tree.peak / 1048576, 1)
        if run == 0:
            png = RESULTS / f"rtl-{name}.png"
            a.send(f"rtl {png}")
            a.wait("RTL_DONE", 30)
    except RuntimeError as e:
        r["error"] = str(e)
    a.quit()
    return r


def chrome_path():
    for p in (r"C:\Program Files\Google\Chrome\Application\chrome.exe", r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"):
        if os.path.exists(p):
            return p
    raise RuntimeError("Google Chrome not found")


def one_chrome(run):
    """The original request: the same page in a plain browser. Headless, fresh profile, no window."""
    events, commands = queue.Queue(), queue.Queue()

    class H(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *x, **k):
            super().__init__(*x, directory=str(ROOT / "web"), **k)

        def log_message(self, *x):
            pass

        def do_GET(self):
            if self.path == "/next":
                try:
                    c = commands.get(timeout=0.2)
                except queue.Empty:
                    c = "wait"
                self.send_response(200)
                self.send_header("Content-Length", str(len(c)))
                self.end_headers()
                self.wfile.write(c.encode())
            else:
                super().do_GET()

        def do_POST(self):
            n = int(self.headers.get("Content-Length", 0))
            events.put((time.perf_counter(), self.rfile.read(n).decode()))
            self.send_response(204)
            self.end_headers()

    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    prof = TMP / f"chrome-{run}"
    url = f"http://127.0.0.1:{srv.server_address[1]}/grid.html?auto"
    t0 = time.perf_counter()
    p = subprocess.Popen([chrome_path(), "--headless=new", f"--user-data-dir={prof}", "--no-first-run", "--no-default-browser-check",
                          "--window-size=1280,800", "--disable-extensions", "--disable-sync", url],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=NO_WINDOW)
    tree = Tree(p.pid)
    r = {"stack": "chrome-headless", "run": run}

    def wait(word, timeout=120):
        end = time.time() + timeout
        while True:
            t, line = events.get(timeout=max(0.1, end - time.time()))
            if line.startswith(word):
                return t, line

    try:
        t, _ = wait("READY")
        r["first_screen_ms"] = round((t - t0) * 1000)
        time.sleep(SETTLE)
        r["idle"] = tree.steady()
        commands.put("table")
        _, line = wait("TABLE_READY")
        r["table_ready_ms"] = float(line.split()[1])
        commands.put("bench")
        _, line = wait("BENCH", 300)
        r.update(fields(line))
        time.sleep(SETTLE)
        r["table"] = tree.steady()
        r["peak_private_mb"] = round(tree.peak / 1048576, 1)
    except queue.Empty:
        r["error"] = "timed out"
    commands.put("quit")
    time.sleep(0.5)
    tree.stop = True
    for q in [psutil.Process(p.pid)] + list(tree.known.values()):
        try:
            q.terminate()
        except psutil.Error:
            pass
    try:
        p.wait(10)
    except subprocess.TimeoutExpired:
        p.kill()
    srv.shutdown()
    time.sleep(1)
    shutil.rmtree(prof, ignore_errors=True)
    return r


def summarise(rows, keys):
    out = {}
    for name in dict.fromkeys(r["stack"] for r in rows):
        rs = [r for r in rows if r["stack"] == name and "error" not in r]
        s = {"runs_ok": len(rs), "runs_failed": len([r for r in rows if r["stack"] == name and "error" in r])}
        for label, get in keys.items():
            v = [get(r) for r in rs if get(r) is not None]
            if v:
                s[label] = {"median": round(statistics.median(v), 1), "min": round(min(v), 1), "max": round(max(v), 1)}
        out[name] = s
    return out


def compare(runs, only=None):
    RESULTS.mkdir(exist_ok=True)
    TMP.mkdir(exist_ok=True)
    rows = []
    st = stacks()
    names = [n for n in list(st) + ["chrome-headless"] if not only or n == only]
    for run in range(runs):  # round robin, so slow moments of the PC fall on every stack alike
        for name in names:
            c = commit_pct()
            if c >= STOP_AT:
                raise SystemExit("stopped: Windows commit reached 96%")
            r = one_chrome(run) if name == "chrome-headless" else one(name, st[name](run), run)
            r["commit_before"] = round(c, 1)
            rows.append(r)
            print(json.dumps(r, ensure_ascii=False), flush=True)
            time.sleep(1.5)
    keys = {
        "first_screen_ms": lambda r: r.get("first_screen_ms"),
        "first_screen_ms_after_run1": lambda r: r.get("first_screen_ms") if r["run"] > 0 else None,
        "idle_private_mb": lambda r: r["idle"]["private_mb"],
        "idle_ws_mb": lambda r: r["idle"]["ws_mb"],
        "idle_procs": lambda r: r["idle"]["procs"],
        "table_private_mb": lambda r: r["table"]["private_mb"],
        "table_ws_mb": lambda r: r["table"]["ws_mb"],
        "peak_private_mb": lambda r: r["peak_private_mb"],
        "table_ready_ms": lambda r: r["table_ready_ms"],
        "jump_med_ms": lambda r: r.get("jump_med"),
        "jump_p95_ms": lambda r: r.get("jump_p95"),
        "wheel_med_ms": lambda r: r.get("wheel_med"),
        "wheel_p95_ms": lambda r: r.get("wheel_p95"),
    }
    if not only:
        (RESULTS / "compare-raw.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
        (RESULTS / "compare-summary.json").write_text(json.dumps(summarise(rows, keys), indent=1), encoding="utf-8")
    print(json.dumps(summarise(rows, keys), indent=1))


def lazy_once(run, lean):
    """One life of the hybrid core: every expert starts on request and leaves after."""
    xvc = str(TMP / "syn-demo.xvc")
    exe = str(ROOT / "hybrid-shell/bin/Release/net8.0-windows/win-x64/exp-hybrid.exe")
    a = App([exe, "--udf", str(TMP / ("udf-hybrid-lean" if lean else "udf-hybrid"))] + (["--lean"] if lean else []))
    r = {"stack": "hybrid-lean" if lean else "hybrid", "run": run}

    def step(name, cmd, word, settle=SETTLE, timeout=120):
        a.send(cmd)
        _, line = a.wait(word, timeout)
        time.sleep(settle)
        parts = line.split()
        r[name] = {"mem": a.tree.steady()}
        if len(parts) > 1:
            try:
                r[name]["ms"] = float(parts[1])
            except ValueError:
                r[name]["note"] = parts[1]
        if len(parts) > 2:
            r[name]["note"] = " ".join(parts[2:])

    try:
        t, _ = a.wait("READY")
        r["first_screen_ms"] = round((t - a.t0) * 1000)
        time.sleep(SETTLE)
        r["core_idle"] = {"mem": a.tree.steady()}
        step("grid_open", "grid", "GRID_READY")
        step("grid_closed", "gridclose", "GRID_CLOSED")
        step("web_open", "web", "WEB_READY")
        step("web_low", "weblow", "WEB_LOW")
        step("web_suspended", "websuspend", "WEB_SUSPENDED")
        step("web_resumed", "webresume", "WEB_RESUMED", settle=2)
        step("web_closed", "webclose", "WEB_CLOSED", settle=8)
        step("worker_open", "worker", "WORKER_READY", settle=2)
        step("worker_closed", "workerclose", "WORKER_CLOSED", settle=2)
        step("plugin_open", "plugin", "PLUGIN_READY", settle=2)
        step("plugin_closed", "pluginclose", "PLUGIN_CLOSED", settle=2)
        step("viewer_open", "viewer " + xvc, "VIEWER_READY")
        a.send("viewerbench")
        _, line = a.wait("VIEWER_BENCH", 120)
        r["viewer_bench"] = fields(line)
        time.sleep(2)
        r["viewer_after_bench"] = {"mem": a.tree.steady()}
        step("viewer_closed", "viewerclose", "VIEWER_CLOSED")
        step("viewerproc_open", "viewerproc " + xvc, "VIEWERPROC_READY")
        step("viewerproc_closed", "viewerprocclose", "VIEWERPROC_CLOSED")
        r["peak_private_mb"] = round(a.tree.peak / 1048576, 1)
    except RuntimeError as e:
        r["error"] = str(e)
    a.quit()
    return r


def lazy(runs):
    RESULTS.mkdir(exist_ok=True)
    TMP.mkdir(exist_ok=True)
    rows = []
    for run in range(runs):
        for lean in (False, True):
            r = lazy_once(run, lean)
            r["commit_before"] = round(commit_pct(), 1)
            rows.append(r)
            print(json.dumps(r, ensure_ascii=False), flush=True)
            time.sleep(1.5)
    (RESULTS / "lazy-raw.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "compare":
        compare(int(sys.argv[2]))
    elif mode == "smoke":
        compare(1, sys.argv[2])
    elif mode == "lazy":
        lazy(int(sys.argv[2]))
