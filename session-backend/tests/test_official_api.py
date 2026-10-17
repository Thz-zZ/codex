"""Integration tests use only pytest-owned empty directories and synthetic messages."""
import json
import os
from pathlib import Path
import sqlite3

import pytest

from conftest import add_session
from session_manager.rpc import CodexRpc, discover_codex
from session_manager.store import inventory
from session_manager.deletion import DeletionService

pytestmark = pytest.mark.skipif(not discover_codex(), reason="No local Codex executable")


@pytest.fixture
def official(tmp_path):
    home = (tmp_path / 'isolated-official-codex').resolve()
    home.mkdir()
    rpc = CodexRpc(discover_codex(), home)
    rpc.call('thread/list', {'useStateDbOnly': True})
    yield home, rpc
    rpc.close()


def isolated_service(home, rpc):
    # Only the integration fixture may bypass the global external-process guard.
    # It never copies or points to the user's real data.
    assert home.name == 'isolated-official-codex'
    assert home.resolve() != (Path.home()/'.codex').resolve()
    return DeletionService(home,rpc,guard=lambda pid: [])


def test_official_single_and_archived_batch(official):
    home,rpc=official
    a=add_session(home,'单个删除')
    b=add_session(home,'归档删除',archived=True)
    c=add_session(home,'普通删除')
    untouched=add_session(home,'保留会话')
    project=home/'project'
    project.mkdir()
    (project/'keep.txt').write_text('keep')
    service=isolated_service(home,rpc)
    result=service.execute(service.prepare({a}))
    assert result.succeeded == [a], result
    result=service.execute(service.prepare({b,c}))
    assert set(result.succeeded)=={b,c},result
    assert set(inventory(home).sessions)=={untouched}
    assert (project/'keep.txt').read_text()=='keep'


def test_official_parent_child_cascade(official):
    home,rpc=official
    root=add_session(home,'父会话')
    child=add_session(home,'子会话',parent=root)
    grandchild=add_session(home,'归档孙会话',parent=child,archived=True)
    service=isolated_service(home,rpc)
    plan=service.prepare({root,child})
    assert plan.affected=={root,child,grandchild}
    result=service.execute(plan)
    assert set(result.succeeded)=={root,child,grandchild},result


def test_official_missing_file_record(official):
    home,rpc=official
    sid=add_session(home,'文件缺失',file=False)
    service=isolated_service(home,rpc)
    result=service.execute(service.prepare({sid}))
    assert result.succeeded==[sid],result


def test_official_delete_also_cleans_persistent_desktop_catalog(official):
    from test_catalog import create_catalog, local_ids
    home,rpc=official
    root=add_session(home,'删除父会话')
    child=add_session(home,'删除子会话',parent=root,archived=True)
    keep=add_session(home,'保留')
    path=create_catalog(home,[root,child,keep])
    service=isolated_service(home,rpc)
    result=service.execute(service.prepare({root}))
    assert set(result.succeeded)=={root,child},result
    assert not result.failures
    assert local_ids(path)=={keep}
    assert set(inventory(home).sessions)=={keep}


def test_official_reads_imported_native_session(official, tmp_path):
    from session_manager.transfer import export_bundle, inspect_bundle, ImportService
    home, rpc = official
    sid = add_session(home, '导入后仍可读取')
    package = tmp_path / 'sessions.zip'
    export_bundle(home, {sid}, package)
    target = tmp_path / 'isolated-import-target'
    target.mkdir()
    target_rpc = CodexRpc(discover_codex(), target)
    target_rpc.call('thread/list', {'useStateDbOnly': True})
    target_rpc.close()
    assert target.resolve() != (Path.home() / '.codex').resolve()
    result = ImportService(target, guard=lambda: []).execute(inspect_bundle(target, package))
    assert result.succeeded == [sid] and not result.failures
    try:
        response = target_rpc.call('thread/read', {'threadId': sid, 'includeTurns': True})
        assert response['thread']['id'] == sid
        assert response['thread']['turns']
        resumed = target_rpc.call('thread/resume', {'threadId': sid, 'cwd': str(target), 'approvalPolicy': 'never', 'sandbox': 'read-only'})
        assert resumed['thread']['id'] == sid
    finally:
        target_rpc.close()


def test_official_reads_imported_database_history(official, tmp_path):
    from session_manager.transfer import export_bundle, inspect_bundle, ImportService
    home, rpc = official
    schema = (Path(__file__).parent / 'history_schema.sql').read_text(encoding='utf-8')
    with sqlite3.connect(home / 'thread_history_1.sqlite') as con:
        con.executescript(schema)
    sid = add_session(home, '数据库会话导出导入', mode='paginated', file=True)
    path = Path(next(iter(inventory(home).sessions[sid].paths)))
    events = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]
    events[0]['payload'].update({'history_mode': 'paginated', 'session_id': sid})
    for ordinal, event in enumerate(events):
        event['ordinal'] = ordinal
    path.write_text('\n'.join(json.dumps(e) for e in events) + '\n', encoding='utf-8')
    with sqlite3.connect(home / 'thread_history_1.sqlite') as con:
        con.execute('INSERT INTO thread_history_projection_state VALUES (?,?,?)', (sid, path.stat().st_size, len(events)))
        con.execute('UPDATE thread_items SET rollout_ordinal=rollout_ordinal+100 WHERE thread_id=?', (sid,))
        con.execute('UPDATE thread_items SET rollout_ordinal=rollout_ordinal-99 WHERE thread_id=?', (sid,))
    # The export source itself must be readable before judging the round trip.
    assert len(rpc.call('thread/items/list', {'threadId': sid, 'limit': 20})['data']) == 4
    # A newer journal references the earlier file by the UUID in its filename.
    # Renaming either file during import would break official history loading.
    import uuid
    storage_id = str(uuid.uuid4())
    tail = path.with_name(f'rollout-2026-09-14T12-00-02-{sid}_{storage_id}.jsonl')
    meta = json.loads(json.dumps(events[0]))
    meta['ordinal'] = len(events)
    meta['payload']['history_base'] = {'thread_id': sid, 'end_ordinal_exclusive': len(events), 'end_byte_offset': path.stat().st_size}
    tail.write_text(json.dumps(meta) + '\n', encoding='utf-8')
    with sqlite3.connect(home / 'state_5.sqlite') as con:
        con.execute('UPDATE threads SET rollout_path=? WHERE id=?', (str(tail), sid))
    assert len(rpc.call('thread/items/list', {'threadId': sid, 'limit': 20})['data']) == 4
    package = tmp_path / 'database.zip'
    export_bundle(home, {sid}, package)
    target = tmp_path / 'isolated-database-import'
    target.mkdir()
    target_rpc = CodexRpc(discover_codex(), target)
    target_rpc.call('thread/list', {'useStateDbOnly': True})
    target_rpc.close()
    # Use only schema/migration metadata from the initialized synthetic source.
    # This follows the installed Codex version without copying any thread rows.
    with sqlite3.connect(home / 'thread_history_1.sqlite') as source:
        ddl = [r[0] for r in source.execute(
            "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type='table' DESC, rowid")]
        migrations = source.execute('SELECT * FROM _sqlx_migrations').fetchall()
    with sqlite3.connect(target / 'thread_history_1.sqlite') as con:
        con.executescript(';\n'.join(ddl)+';')
        con.executemany('INSERT INTO _sqlx_migrations VALUES (?,?,?,?,?,?)', migrations)
    result = ImportService(target, guard=lambda: []).execute(inspect_bundle(target, package))
    assert result.succeeded == [sid] and not result.failures
    try:
        response = target_rpc.call('thread/items/list', {'threadId': sid, 'limit': 20})
        assert len(response['data']) == 4
    finally:
        target_rpc.close()


def test_official_paginated_database_only_record(official):
    home,rpc=official
    schema=(Path(__file__).parent/'history_schema.sql').read_text(encoding='utf-8')
    with sqlite3.connect(home/'thread_history_1.sqlite') as con:
        con.executescript(schema)
    sid=add_session(home,'数据库会话',mode='paginated',file=False)
    service=isolated_service(home,rpc)
    assert inventory(home).sessions[sid].has_history
    result=service.execute(service.prepare({sid}))
    assert result.succeeded==[sid],result
    with sqlite3.connect(home/'thread_history_1.sqlite') as con:
        assert con.execute('SELECT count(*) FROM thread_items WHERE thread_id=?',(sid,)).fetchone()[0]==0


@pytest.mark.skipif(os.name!='nt',reason='Windows sharing violation')
def test_official_locked_file_is_reported(official):
    import ctypes
    from ctypes import wintypes
    home,rpc=official
    sid=add_session(home,'文件占用测试')
    s=inventory(home).sessions[sid]
    path=next(iter(s.paths))
    service=isolated_service(home,rpc)
    plan=service.prepare({sid})
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,wintypes.LPVOID,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    kernel.CreateFileW.restype=wintypes.HANDLE
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    # Readable by others, but FILE_SHARE_DELETE is deliberately withheld.
    handle=kernel.CreateFileW(path,0x80000000,1|2,None,3,0,None)
    assert handle != wintypes.HANDLE(-1).value
    try:
        result=service.execute(plan)
        assert sid in result.failures and sid not in result.succeeded
        assert Path(path).exists()
    finally:
        kernel.CloseHandle(handle)
