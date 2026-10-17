"""Bridge behavior uses synthetic homes only; no user data or clients."""
import json
from pathlib import Path
import sqlite3
import time

import pytest

from conftest import add_session, STATE_SCHEMA
from service import SessionService
from session_manager.store import inventory
from session_manager.deletion import DeletionBlocked
from test_deletion import FakeRpc


class BridgeRpc(FakeRpc):
    def close(self):
        pass


def service(home, guard=lambda *args: []):
    return SessionService(home, 'test-codex.exe',
                          rpc_factory=lambda exe, path: BridgeRpc(path), guard=guard)


def test_missing_home_settings_can_be_repaired(tmp_path):
    s = service(tmp_path / 'missing')
    assert s.dispatch('status', {})['home'].endswith('missing')
    with pytest.raises(ValueError, match='数据目录不存在'):
        s.dispatch('list', {})
    assert not s.home.exists()


def test_inventory_preview_and_bound_pagination(home):
    first = add_session(home, '分页中文 <script> 标签', count=130)
    second = add_session(home, '归档记录', archived=True)
    s = service(home)
    result = s.dispatch('list', {})
    assert result['counts']['total'] == 2 and result['counts']['archived'] == 1
    page = s.dispatch('preview', {'id': first})
    assert len(page['messages']) == 60 and page['cursor']
    assert '<script>' in page['messages'][0]['text']
    with pytest.raises(ValueError, match='分页已变化'):
        s.dispatch('preview', {'id': second, 'cursor': page['cursor']})
    with pytest.raises(ValueError, match='分页已变化'):
        s.dispatch('preview', {'id': first, 'tools': True, 'cursor': page['cursor']})
    rest = s.dispatch('preview', {'id': first, 'cursor': page['cursor']})
    assert rest['messages'][0]['text'] != page['messages'][0]['text']


@pytest.mark.parametrize('offline', [False, True])
def test_list_scans_local_inventory_once_and_keeps_fallback(home, offline):
    sid = add_session(home)
    s = service(home)
    scans = []
    reader = s.reader
    def scan(*args, **kwargs):
        scans.append(1)
        return reader(*args, **kwargs)
    s.reader = scan
    if offline:
        def unavailable():
            raise RuntimeError('offline fixture')
        s.rpc.list_threads = unavailable
    result = s.list_sessions()
    assert len(scans) == 1 and result['sessions'][0]['id'] == sid
    assert bool(result['warnings']) == offline


def test_journal_pagination_releases_old_states_and_replays_last_request(home, monkeypatch):
    sid = add_session(home, mode='paginated', count=4)
    other = add_session(home, count=130)
    path = Path(next(iter(inventory(home).sessions[sid].paths)))
    events = [{'type': 'session_meta', 'payload': {'id': sid, 'session_id': sid}}]
    for i in range(604):
        event = {'type': 'event_msg', 'payload': {'type': 'item_completed',
            'item': {'type': 'agentMessage', 'id': str(i), 'text': f'journal {i}'}}}
        events.extend([event, event])
    path.write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8')
    s = service(home)
    page = s.preview({'id': sid})
    messages, shared_seen = list(page['messages']), None
    first_token = page['cursor']
    def failed_read(*args, **kwargs):
        raise OSError('retry fixture')
    with monkeypatch.context() as m:
        m.setattr('service.PreviewReader.page', failed_read)
        with pytest.raises(OSError, match='retry fixture'):
            s.preview({'id': sid, 'cursor': first_token})
    assert s.tokens[first_token][2]['result'] is None
    for _ in range(20):
        token = page['cursor']
        if token is None:
            break
        state = s.tokens[token][2]['cursor']
        if shared_seen is None:
            shared_seen = state['seen']
        assert state['seen'] is shared_seen
        page = s.preview({'id': sid, 'cursor': token})
        assert s.preview({'id': sid, 'cursor': token}) == page
        messages.extend(page['messages'])
        assert sum(entry[1] == 'cursor' for entry in s.tokens.values()) <= 2
    assert page['cursor'] is None
    assert len(messages) == 604 and len({m['text'] for m in messages}) == 604
    assert all(entry[2]['cursor'] is None for entry in s.tokens.values() if entry[1] == 'cursor')
    s.preview({'id': other})
    assert sum(entry[1] == 'cursor' for entry in s.tokens.values()) == 1


def test_delete_requires_review_and_consumes_plan(home):
    parent = add_session(home, '父会话')
    child = add_session(home, '子会话', parent=parent)
    keep = add_session(home, '保留会话')
    s = service(home)
    plan = s.dispatch('prepareDelete', {'ids': [parent]})
    assert plan['selected'] == 1 and plan['affected'] == 2
    assert {row['id'] for row in plan['sessions']} == {parent, child}
    with pytest.raises(ValueError, match='确认永久删除'):
        s.dispatch('executeDelete', {'token': plan['token']})
    assert len(inventory(home).sessions) == 3
    result = s.dispatch('executeDelete', {'token': plan['token'], 'confirmed': True})
    assert set(result['succeeded']) == {parent, child} and not result['failures']
    assert set(inventory(home).sessions) == {keep}
    with pytest.raises(ValueError, match='已过期'):
        s.dispatch('executeDelete', {'token': plan['token'], 'confirmed': True})


def test_cancel_and_expiry_do_not_delete(home):
    sid = add_session(home)
    s = service(home)
    plan = s.dispatch('prepareDelete', {'ids': [sid]})
    s.dispatch('cancelPlan', {'token': plan['token']})
    with pytest.raises(ValueError, match='已过期'):
        s.dispatch('executeDelete', {'token': plan['token'], 'confirmed': True})
    plan = s.dispatch('prepareDelete', {'ids': [sid]})
    _, kind, value = s.tokens[plan['token']]
    s.tokens[plan['token']] = (time.monotonic() - 601, kind, value)
    with pytest.raises(ValueError, match='已过期'):
        s.dispatch('executeDelete', {'token': plan['token'], 'confirmed': True})
    assert sid in inventory(home).sessions


def test_external_client_blocks_preparation(home):
    sid = add_session(home)
    s = service(home, guard=lambda *args: ['external Codex client'])
    with pytest.raises(DeletionBlocked, match='关闭 Codex'):
        s.dispatch('prepareDelete', {'ids': [sid]})
    assert not s.tokens and sid in inventory(home).sessions


def test_export_import_review_and_skip_existing(home, tmp_path):
    sid = add_session(home, '中文会话导出')
    source = service(home)
    package = tmp_path / '中文导出.zip'
    assert source.dispatch('export', {'ids': [sid], 'path': str(package)})['count'] == 1
    target = tmp_path / 'target'
    target.mkdir()
    with sqlite3.connect(target / 'state_5.sqlite') as con:
        con.executescript(STATE_SCHEMA)
    s = service(target)
    plan = s.dispatch('prepareImport', {'path': str(package)})
    assert plan['importing'] == 1 and plan['existing'] == 0
    with pytest.raises(ValueError, match='核对导入'):
        s.dispatch('executeImport', {'token': plan['token']})
    assert not inventory(target).sessions
    result = s.dispatch('executeImport', {'token': plan['token'], 'confirmed': True})
    assert result['succeeded'] == [sid] and not result['failures']
    again = s.dispatch('prepareImport', {'path': str(package)})
    assert again['existing'] == 1 and again['importing'] == 0
    result = s.dispatch('executeImport', {'token': again['token'], 'confirmed': True})
    assert result['skipped'] == [sid] and not result['succeeded']
