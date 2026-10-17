import hashlib
import json
from pathlib import Path
import sqlite3
import zipfile

import pytest

from conftest import add_session, STATE_SCHEMA, HISTORY_SCHEMA
from session_manager.store import inventory, PreviewReader
from session_manager.transfer import export_bundle, inspect_bundle, ImportService


def target_home(tmp_path, history=False):
    p = tmp_path / 'target'
    p.mkdir()
    with sqlite3.connect(p / 'state_5.sqlite') as con:
        con.executescript(STATE_SCHEMA)
    if history:
        with sqlite3.connect(p / 'thread_history_1.sqlite') as con:
            con.executescript(HISTORY_SCHEMA)
    return p


def test_roundtrip_legacy_archived_database_and_parent_child(home, tmp_path):
    parent = add_session(home, '父会话')
    child = add_session(home, '归档子会话', parent=parent, archived=True)
    database = add_session(home, '数据库对话', mode='paginated', file=False)
    keep = add_session(home, '不导出')
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {parent, child, database}, bundle)
    target = target_home(tmp_path, history=True)
    plan = inspect_bundle(target, bundle)
    assert not plan.existing
    result = ImportService(target, guard=lambda: []).execute(plan)
    assert set(result.succeeded) == {parent, child, database} and not result.failures
    inv = inventory(target)
    assert keep not in inv.sessions and inv.sessions[child].archived
    assert inv.affected({parent}) == {parent, child}
    for sid in result.succeeded:
        assert len(PreviewReader(target).page(inv.sessions[sid]).messages) == 4
        assert all(Path(p).is_relative_to(target) for p in inv.sessions[sid].paths)


def test_import_existing_session_is_not_overwritten(home, tmp_path):
    sid = add_session(home, '原始会话')
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    with sqlite3.connect(home / 'state_5.sqlite') as con:
        con.execute('UPDATE threads SET title=? WHERE id=?', ('保留新标题', sid))
    plan = inspect_bundle(home, bundle)
    result = ImportService(home, guard=lambda: []).execute(plan)
    assert result.skipped == [sid] and not result.succeeded
    assert inventory(home).sessions[sid].title == '保留新标题'


def test_schema_mismatch_is_blocked_before_any_write(home, tmp_path):
    sid = add_session(home)
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    target = target_home(tmp_path)
    with sqlite3.connect(target / 'state_5.sqlite') as con:
        con.execute('ALTER TABLE threads ADD COLUMN incompatible TEXT')
    with pytest.raises(ValueError, match='存储结构'):
        ImportService(target, guard=lambda: []).execute(inspect_bundle(target, bundle))
    assert not inventory(target).sessions
    assert not (target / 'sessions').exists()


def test_corrupt_bundle_is_rejected_without_writes(home, tmp_path):
    sid = add_session(home)
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    with zipfile.ZipFile(bundle) as z:
        files = {name: z.read(name) for name in z.namelist()}
    name = next(n for n in files if n.startswith('files/'))
    files[name] += b'corrupt'
    with zipfile.ZipFile(bundle, 'w') as z:
        for name, data in files.items():
            z.writestr(name, data)
    target = target_home(tmp_path)
    with pytest.raises(ValueError, match='校验失败'):
        inspect_bundle(target, bundle)
    assert not inventory(target).sessions


def test_external_client_blocks_import(home, tmp_path):
    sid = add_session(home)
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    target = target_home(tmp_path)
    with pytest.raises(RuntimeError, match='关闭 Codex'):
        ImportService(target, guard=lambda: ['codex.exe']).execute(inspect_bundle(target, bundle))
    assert not inventory(target).sessions


def test_import_failure_rolls_back_history_and_files(home, tmp_path):
    sid = add_session(home, mode='paginated')
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    target = target_home(tmp_path, history=True)
    calls = 0
    def guard():
        nonlocal calls
        calls += 1
        return ['new writer'] if calls == 4 else []
    result = ImportService(target, guard=guard).execute(inspect_bundle(target, bundle))
    assert sid in result.failures and not result.succeeded
    assert not inventory(target).sessions
    assert not list(target.rglob('*.jsonl'))


def test_export_rejects_changed_source_and_preserves_previous_file(home, tmp_path):
    sid = add_session(home)
    bundle = tmp_path / 'export.zip'
    bundle.write_bytes(b'previous export')
    path = Path(next(iter(inventory(home).sessions[sid].paths)))
    def progress(*args):
        with path.open('a') as f:
            f.write('\n')
    with pytest.raises(RuntimeError, match='发生变化'):
        export_bundle(home, {sid}, bundle, progress)
    assert bundle.read_bytes() == b'previous export'


def test_archive_member_cannot_choose_destination_path(home, tmp_path):
    sid = add_session(home)
    bundle = tmp_path / 'export.zip'
    export_bundle(home, {sid}, bundle)
    with zipfile.ZipFile(bundle) as z:
        files = {name: z.read(name) for name in z.namelist()}
    old = next(n for n in files if n.startswith('files/'))
    bad = f'files/{sid}/../../outside.jsonl'
    files[bad] = files.pop(old)
    name = f'sessions/{sid}.json'
    record = json.loads(files[name])
    record['files'][0]['member'] = bad
    files[name] = json.dumps(record).encode()
    manifest = json.loads(files['manifest.json'])
    manifest['members'][bad] = manifest['members'].pop(old)
    manifest['members'][name] = {'sha256': hashlib.sha256(files[name]).hexdigest(), 'size': len(files[name])}
    files['manifest.json'] = json.dumps(manifest).encode()
    with zipfile.ZipFile(bundle, 'w') as z:
        for name, data in files.items():
            z.writestr(name, data)
    target = target_home(tmp_path)
    with pytest.raises(ValueError, match='非法文件路径'):
        inspect_bundle(target, bundle)
    assert not inventory(target).sessions
