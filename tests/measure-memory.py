"""Measure the isolated UI smoke test, including all of its child processes."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time

import psutil

parser = argparse.ArgumentParser()
parser.add_argument('executable', type=Path)
args = parser.parse_args()
executable = args.executable.resolve()
output = executable.parent / 'smoke-data'
subprocess.run([sys.executable, str(Path(__file__).with_name('seed-session-fixture.py')),
                str(output / 'session-fixture')], check=True)
startup = subprocess.STARTUPINFO()
startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0
child = subprocess.Popen([str(executable), '--smoke-test'], startupinfo=startup)
root = psutil.Process(child.pid)
phases = {}
started = time.monotonic()
started_wall = time.time()
try:
    while child.poll() is None:
        if time.monotonic() - started > 180:
            raise TimeoutError('UI smoke test exceeded 180 seconds')
        working, private, names = 0, 0, []
        try:
            processes = [root, *root.children(recursive=True)]
        except psutil.NoSuchProcess:
            break
        for process in processes:
            try:
                memory = process.memory_info()
                names.append(process.name().lower())
                working += memory.rss
                private += memory.private
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        phase = 'startup'
        phase_file = output / 'window-phase.json'
        try:
            if phase_file.stat().st_mtime >= started_wall:
                reported_phase = json.loads(phase_file.read_text(encoding='utf-8')).get('phase')
                if reported_phase in ('visible', 'tray', 'reopened'):
                    phase = reported_phase
        except (OSError, ValueError):
            pass
        sample = {'workingSetMiB': round(working / 1048576, 2),
                  'privateMiB': round(private / 1048576, 2), 'processes': len(names)}
        record = phases.setdefault(phase, {'peakWorkingSetMiB': 0, 'peakPrivateMiB': 0, 'samples': 0})
        record['peakWorkingSetMiB'] = max(record['peakWorkingSetMiB'], sample['workingSetMiB'])
        record['peakPrivateMiB'] = max(record['peakPrivateMiB'], sample['privateMiB'])
        record['last'] = sample
        record['samples'] += 1
        time.sleep(0.05)
    code = child.wait(timeout=10)
    smoke_report = output / 'smoke-result.json'
    if code == 0 and (not smoke_report.exists() or smoke_report.stat().st_mtime < started_wall
                      or not json.loads(smoke_report.read_text(encoding='utf-8')).get('ok')):
        raise RuntimeError('The isolated UI smoke test did not produce a successful fresh report')
except BaseException:
    try:
        for process in reversed(root.children(recursive=True)):
            process.kill()
        root.kill()
    except psutil.NoSuchProcess:
        pass
    child.wait(timeout=10)
    raise
result = {'exitCode': code, 'elapsedSeconds': round(time.monotonic() - started, 2),
          'measurement': 'Sum of host and recursive children; working sets may include shared pages.',
          'phases': phases}
(output / 'process-memory.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
raise SystemExit(code)
