import ctypes
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path


LAUNCHER = Path(__file__).resolve().parents[1] / "run-local.sh"
PR_SET_CHILD_SUBREAPER = 36
PR_GET_CHILD_SUBREAPER = 37

FAKE_TOOL = r'''
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

root = Path(os.environ["FIXTURE_ROOT"])
tool = Path(sys.argv[0]).name
args = sys.argv[1:]
settings = json.loads((root / "settings.json").read_text())

def record(kind, **fields):
    with (root / "processes.jsonl").open("a") as output:
        output.write(json.dumps(dict(kind=kind, pid=os.getpid(), pgid=os.getpgrp(), **fields)) + "\n")

def requested_exit(name):
    marker = root / ("exit-" + name)
    return int(marker.read_text()) if marker.exists() else None

if tool == "date":
    time.sleep(settings.get("slow_date", 0))
    print("2026-10-18 12:00:00")
    sys.exit(0)

if tool == "curl":
    record("probe", args=args)
    name = "api" if ":18080/" in args[-1] else "procursor"
    sys.exit(1 if settings.get("readiness_failure") or settings.get("early_exit") == name else 0)

if tool == "worker":
    name = args[0]
    record("application", name=name)
    stopping = False
    def stop(signum, frame):
        global stopping
        stopping = True
    signal.signal(signal.SIGTERM, signal.SIG_IGN if settings.get("stubborn") == name else stop)
    signal.signal(signal.SIGINT, stop)
    if settings.get("early_exit") == name:
        print(name + " exited during startup", flush=True)
        sys.exit(settings.get("early_exit_code", 1))
    while not stopping:
        code = requested_exit(name)
        if code is not None:
            if name == "api":
                for index in range(settings.get("buffered_lines", 0)):
                    print(name + " buffered " + str(index), flush=True)
            print(name + " final output", flush=True)
            sys.exit(code)
        time.sleep(0.02)
    print(name + " stopped", flush=True)
    sys.exit(0)

if tool == "dotnet" and args[0] == "build":
    record("build", args=args)
    if settings.get("hold_build"):
        while not (root / "release-build").exists():
            time.sleep(0.02)
    sys.exit(settings.get("build_exit", 0))

if tool == "dotnet" and args[0] == "run":
    name = "procursor" if "ProCursor.Service" in args[args.index("--project") + 1] else "api"
elif tool == "npm" and args[:2] == ["run", "dev"]:
    name = "ui"
else:
    record("unexpected", args=args)
    sys.exit(99)

record("wrapper", name=name, args=args, tmpdir=os.environ.get("TMPDIR"),
       db=os.environ.get("DB_CONNECTION_STRING"),
       procursor_db=os.environ.get("PROCURSOR_DB_CONNECTION_STRING"),
       urls=os.environ.get("ASPNETCORE_URLS"),
       shared_key=os.environ.get("PROCURSOR_SHARED_KEY"),
       api_base=os.environ.get("PROCURSOR_PROPR_BASE_URL"),
       procursor_base=os.environ.get("PROCURSOR_SERVICE_BASE_URL"),
       vite_api=os.environ.get("VITE_API_BASE_URL"))
child = subprocess.Popen([str(root / "tools" / "worker"), name])
while child.poll() is None:
    code = requested_exit(name + "-wrapper")
    if code is not None:
        sys.exit(code)
    time.sleep(0.02)
sys.exit(child.returncode)
'''


def process_state(pid):
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return fields[0], fields[19]
    except (FileNotFoundError, ProcessLookupError):
        return None


class RunLocalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.libc = ctypes.CDLL(None, use_errno=True)
        previous = ctypes.c_int()
        if cls.libc.prctl(PR_GET_CHILD_SUBREAPER, ctypes.byref(previous), 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), "Could not read child subreaper state")
        cls.previous_subreaper = previous.value
        if cls.libc.prctl(PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), "Could not own orphaned fixture descendants")

    @classmethod
    def tearDownClass(cls):
        cls.libc.prctl(PR_SET_CHILD_SUBREAPER, cls.previous_subreaper, 0, 0, 0)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        (self.repo / "scripts").mkdir()
        self.launcher = self.repo / "scripts" / "run-local.sh"
        shutil.copyfile(LAUNCHER, self.launcher)
        for project in ("MeisterDev.ProPR.Api", "MeisterDev.ProPR.ProCursor.Service"):
            directory = self.repo / "src" / project
            directory.mkdir(parents=True)
            (directory / f"{project}.csproj").touch()
        (self.repo / "frontend").mkdir()
        self.tools = self.repo / "tools"
        self.tools.mkdir()
        for name in ("dotnet", "npm", "curl", "worker"):
            tool = self.tools / name
            tool.write_text(f"#!{sys.executable}\n" + FAKE_TOOL)
            tool.chmod(0o755)
        self.caller_tmpdir = self.repo / "caller-tmp"
        self.caller_tmpdir.mkdir()
        self.env = {
            "PATH": f"{self.tools}:{os.defpath}",
            "HOME": str(self.repo / "home"),
            "TMPDIR": str(self.caller_tmpdir),
            "MEISTER_JWT_SECRET": "fixture-jwt",
            "PROCURSOR_SHARED_KEY": "fixture-shared-key",
            "FIXTURE_ROOT": str(self.repo),
            "RUN_LOCAL_LOG_FILE": str(self.repo / "run.log"),
            "RUN_LOCAL_BACKEND_READY_TIMEOUT_SECONDS": "1",
            "RUN_LOCAL_PROCURSOR_READY_TIMEOUT_SECONDS": "1",
        }
        self.process = None
        self.sentinel = None
        self.known = {}
        self.addCleanup(self.cleanup_processes)

    def records(self, kind=None):
        path = self.repo / "processes.jsonl"
        records = [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []
        return [record for record in records if kind is None or record["kind"] == kind]

    def remember_descendants(self, pid):
        state = process_state(pid)
        if state is None:
            return
        self.known[pid] = state[1]
        try:
            children = Path(f"/proc/{pid}/task/{pid}/children").read_text().split()
        except (FileNotFoundError, ProcessLookupError):
            return
        for child in children:
            self.remember_descendants(int(child))

    def remember_processes(self):
        if self.process is not None:
            self.remember_descendants(self.process.pid)
        for record in self.records():
            pid = record["pid"]
            state = process_state(pid)
            if state is not None:
                self.known[pid] = state[1]

    def alive(self, pid):
        state = process_state(pid)
        return state is not None and state[0] != "Z" and state[1] == self.known.get(pid)

    def reap_orphans(self):
        for pid in self.known:
            if self.process is not None and pid == self.process.pid:
                continue
            try:
                os.waitpid(pid, os.WNOHANG)
            except ChildProcessError:
                pass

    def until(self, condition, timeout=4):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.remember_processes()
            self.reap_orphans()
            if condition():
                return
            time.sleep(0.02)
        self.fail("Fixture deadline exceeded:\n" + (self.repo / "terminal.log").read_text())

    def start(self, monitor=False, **settings):
        (self.repo / "settings.json").write_text(json.dumps(settings))
        if "slow_date" in settings:
            tool = self.tools / "date"
            tool.write_text(f"#!{sys.executable}\n" + FAKE_TOOL)
            tool.chmod(0o755)
        if monitor:
            self.env["SHELLOPTS"] = "monitor"
        command = ["bash", "-m"] if monitor else ["bash"]
        terminal = (self.repo / "terminal.log").open("w")
        self.addCleanup(terminal.close)
        self.process = subprocess.Popen(
            command + [str(self.launcher), "Host=fixture.invalid;Database=fixture;Password=fixture-password", "18080", "18081", "--skip-ui-install"],
            cwd=self.repo, env=self.env, stdin=subprocess.DEVNULL, stdout=terminal, stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        if not settings.get("hold_build") and not settings.get("readiness_failure") and not settings.get("early_exit"):
            self.until(lambda: len(self.records("application")) == 3)

    def request_exit(self, name, code):
        pending = self.repo / f"pending-exit-{name}"
        pending.write_text(str(code))
        pending.replace(self.repo / f"exit-{name}")

    def wait_for_exit(self, code, timeout=8):
        self.until(lambda: self.process.poll() is not None, timeout)
        self.assertEqual(code, self.process.returncode, (self.repo / "terminal.log").read_text())
        self.until(lambda: not any(self.alive(pid) for pid in self.known))
        self.assertEqual([], list(self.caller_tmpdir.iterdir()))

    def cleanup_processes(self):
        self.remember_processes()
        groups = {self.process.pid} if self.process is not None else set()
        for pid in self.known:
            if self.alive(pid):
                try:
                    groups.add(os.getpgid(pid))
                except ProcessLookupError:
                    pass
        for group in groups:
            if group != os.getpgrp():
                try:
                    os.killpg(group, signal.SIGKILL)
                except ProcessLookupError:
                    pass
        if self.process is not None:
            self.process.wait(timeout=3)
        if self.sentinel is not None:
            self.sentinel.terminate()
            self.sentinel.wait(timeout=3)
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            try:
                pid, _ = os.waitpid(-1, os.WNOHANG)
                if pid == 0:
                    time.sleep(0.01)
            except ChildProcessError:
                break
        self.assertFalse(any(self.alive(pid) for pid in self.known), "A fixture process survived cleanup")

    def test_clean_api_exit_stops_peers_and_preserves_configuration(self):
        self.sentinel = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True)
        self.start()
        self.request_exit("api", 0)

        self.wait_for_exit(0)

        self.assertIsNone(self.sentinel.poll())
        wrappers = {record["name"]: record for record in self.records("wrapper")}
        self.assertEqual(str(self.caller_tmpdir), wrappers["api"]["tmpdir"])
        self.assertEqual("fixture-shared-key", wrappers["api"]["shared_key"])
        self.assertEqual(wrappers["api"]["shared_key"], wrappers["procursor"]["shared_key"])
        self.assertEqual("http://0.0.0.0:18080", wrappers["api"]["urls"])
        self.assertEqual("http://127.0.0.1:18081", wrappers["api"]["procursor_base"])
        self.assertEqual("http://127.0.0.1:18080", wrappers["procursor"]["api_base"])
        self.assertEqual(wrappers["api"]["db"], wrappers["procursor"]["procursor_db"])
        self.assertEqual("/api", wrappers["ui"]["vite_api"])
        self.assertEqual(["--no-build", "--no-launch-profile"], wrappers["api"]["args"][-2:])
        self.assertEqual(["http://localhost:18080/livez", "http://localhost:18081/healthz"], [record["args"][-1] for record in self.records("probe")])
        self.assertIn("api final output", (self.repo / "run.log").read_text())
        self.assertEqual([], self.records("unexpected"))

    def test_exited_wrapper_still_stops_its_stubborn_application_descendant(self):
        self.start(stubborn="procursor")
        self.request_exit("procursor-wrapper", 7)

        self.wait_for_exit(7)

    def test_failed_procursor_stops_peers_and_preserves_status(self):
        self.start()
        self.request_exit("procursor", 17)

        self.wait_for_exit(17)

    def test_failed_ui_stops_peers_and_preserves_status(self):
        self.start()
        self.request_exit("ui", 19)

        self.wait_for_exit(19)

    def test_build_failure_stops_fifo_readers_and_preserves_status(self):
        self.start(hold_build=True, build_exit=23)
        self.until(lambda: len(self.records("build")) == 1)
        (self.repo / "release-build").touch()

        self.wait_for_exit(23)
        self.assertEqual([], self.records("wrapper"))

    def test_termination_stops_services_and_returns_signal_status(self):
        self.start()
        self.process.send_signal(signal.SIGTERM)

        self.wait_for_exit(143)

    def test_interrupt_stops_services_and_returns_signal_status(self):
        self.start()
        self.process.send_signal(signal.SIGINT)

        self.wait_for_exit(130)

    def test_termination_during_build_stops_command_and_fifo_readers(self):
        self.start(hold_build=True)
        self.until(lambda: len(self.records("build")) == 1)
        self.process.send_signal(signal.SIGTERM)

        self.wait_for_exit(143)

    def test_readiness_failure_stops_services_and_all_fifo_readers(self):
        self.start(readiness_failure=True)

        self.wait_for_exit(1)

    def test_early_api_failure_preserves_actual_status(self):
        self.start(early_exit="api", early_exit_code=29)

        self.wait_for_exit(29)

    def test_early_procursor_failure_preserves_actual_status(self):
        self.start(early_exit="procursor", early_exit_code=31)

        self.wait_for_exit(31)

    def test_clean_exit_before_readiness_remains_a_startup_failure(self):
        self.start(early_exit="api", early_exit_code=0)

        self.wait_for_exit(1)

    def test_inherited_monitor_mode_keeps_services_in_tracked_groups(self):
        self.start(monitor=True)
        self.request_exit("api", 0)

        self.wait_for_exit(0)

    def test_shutdown_drains_buffered_output_after_producer_exits(self):
        self.start(slow_date=0.08, buffered_lines=40)
        self.request_exit("api", 0)

        self.wait_for_exit(0)

        output = (self.repo / "run.log").read_text()
        for index in range(40):
            self.assertIn(f"api buffered {index}\n", output)
        self.assertIn("api final output", output)


if __name__ == "__main__":
    unittest.main()
