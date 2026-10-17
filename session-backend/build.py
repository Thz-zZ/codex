"""Build the private session service; no Qt or separate GUI runtime is needed."""
from pathlib import Path
import argparse
import subprocess
import sys

root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=root.parent/'build')
parser.add_argument('--work', type=Path, default=root.parent/'build'/'session-build')
args = parser.parse_args()
args.work.mkdir(parents=True, exist_ok=True)
subprocess.run([sys.executable, '-m', 'PyInstaller', '--noconfirm', '--onefile', '--console',
                '--name', 'SessionBackend', '--distpath', str(args.output),
                '--workpath', str(args.work/'pyinstaller'), '--specpath', str(args.work),
                '--paths', str(root), '--hidden-import', 'zstandard',
                str(root/'service.py')], check=True, cwd=root)
