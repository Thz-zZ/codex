"""Create synthetic sessions only inside build/smoke-data for native UI QA."""
from pathlib import Path
import json
import sqlite3
import sys

root = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(root/'session-backend'), str(root/'session-backend/tests')]
from conftest import add_session
from session_manager.rpc import CodexRpc, discover_codex
from session_manager.store import inventory

home = (Path(sys.argv[1]) if len(sys.argv) > 1 else root/'build/current/smoke-data/session-fixture').resolve()
assert home.name == 'session-fixture' and home.parent.name == 'smoke-data'
home.mkdir(parents=True, exist_ok=True)
rpc = CodexRpc(discover_codex(), home)
try:
    rpc.call('thread/list', {'useStateDbOnly': True})
finally:
    rpc.close()
if not inventory(home).sessions:
    parent = add_session(home, '界面布局与交互优化', count=132)
    add_session(home, '排版检查 · 派生会话', parent=parent, count=6)
    add_session(home, '发布记录 · v1.0.4', archived=True)
    add_session(home, '数据库会话预览', mode='paginated', file=False, count=6)
    path = Path(next(iter(inventory(home).sessions[parent].paths)))
    lines = path.read_text(encoding='utf-8').splitlines()
    lines.insert(1, json.dumps({'type': 'response_item', 'payload': {
        'type': 'function_call', 'name': 'layout_check',
        'arguments': '{"width":960,"height":650}'}}))
    path.write_text('\n'.join(lines)+'\n', encoding='utf-8')
print('Synthetic session fixture ready:', len(inventory(home).sessions))
