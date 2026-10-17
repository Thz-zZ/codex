from pathlib import Path
import sqlite3

import pytest

from conftest import add_session
from session_manager.deletion import DeletionService, DeletionBlocked, ScopeChanged
from session_manager.store import inventory


class FakeRpc:
    pid = None
    def __init__(self, home):
        self.home = home
        self.calls = []
        self.fail = set()
    def list_threads(self):
        return []
    def call(self, method, params, **kwargs):
        assert method == "thread/delete"
        sid = params["threadId"]
        self.calls.append(sid)
        if sid in self.fail:
            raise RuntimeError("模拟接口失败")
        inv = inventory(self.home)
        for target in inv.affected({sid}):
            if target in inv.sessions:
                for path in inv.sessions[target].paths:
                    Path(path).unlink(missing_ok=True)
            with sqlite3.connect(self.home / "state_5.sqlite") as con:
                con.execute("DELETE FROM threads WHERE id=?", (target,))
                con.execute("DELETE FROM thread_spawn_edges WHERE child_thread_id=? OR parent_thread_id=?", (target,target))
        return {}


def service(home, rpc):
    return DeletionService(home,rpc,guard=lambda pid: [])


def test_single_and_cancel_preparation(home):
    sid = add_session(home)
    rpc = FakeRpc(home)
    s = service(home,rpc)
    plan = s.prepare({sid})
    assert not rpc.calls and sid in inventory(home).sessions
    result = s.execute(plan)
    assert result.succeeded == [sid] and not result.failures
    assert sid not in inventory(home).sessions
    with pytest.raises(ScopeChanged):
        s.prepare({sid})


def test_parent_descendants_deduplicated(home):
    root = add_session(home)
    child = add_session(home,parent=root,archived=True)
    grandchild = add_session(home,parent=child)
    untouched = add_session(home)
    rpc = FakeRpc(home)
    s = service(home,rpc)
    plan = s.prepare({root,child})
    assert plan.affected == {root,child,grandchild}
    result = s.execute(plan)
    assert set(result.succeeded) == {root,child,grandchild}
    assert rpc.calls == [root]
    assert set(inventory(home).sessions) == {untouched}


def test_new_child_requires_reconfirmation(home):
    root = add_session(home)
    rpc = FakeRpc(home)
    s = service(home,rpc)
    plan = s.prepare({root})
    add_session(home,parent=root)
    with pytest.raises(ScopeChanged):
        s.execute(plan)
    assert not rpc.calls


def test_changed_thread_requires_reconfirmation(home):
    root = add_session(home)
    rpc = FakeRpc(home)
    s = service(home,rpc)
    plan = s.prepare({root})
    with sqlite3.connect(home/'state_5.sqlite') as con:
        con.execute('UPDATE threads SET updated_at=updated_at+1 WHERE id=?',(root,))
    with pytest.raises(ScopeChanged):
        s.execute(plan)
    assert not rpc.calls


def test_partial_batch_failure(home):
    a,b = add_session(home),add_session(home)
    rpc = FakeRpc(home)
    rpc.fail.add(a)
    s = service(home,rpc)
    result = s.execute(s.prepare({a,b}))
    assert result.succeeded == [b]
    assert a in result.failures


def test_busy_process_blocks_before_api(home):
    sid = add_session(home)
    rpc = FakeRpc(home)
    s = DeletionService(home,rpc,guard=lambda pid:['codex.exe PID 123'])
    with pytest.raises(DeletionBlocked,match="Codex"):
        s.prepare({sid})
    assert not rpc.calls


def test_external_path_never_deleted(home,tmp_path):
    sid = add_session(home)
    outside = tmp_path/'project.txt'
    outside.write_text('keep')
    with sqlite3.connect(home/'state_5.sqlite') as con:
        con.execute('UPDATE threads SET rollout_path=? WHERE id=?',(str(outside),sid))
    rpc = FakeRpc(home)
    with pytest.raises(DeletionBlocked,match="之外"):
        service(home,rpc).prepare({sid})
    assert outside.read_text() == 'keep' and not rpc.calls


def test_process_starts_after_confirmation(home):
    sid = add_session(home)
    rpc = FakeRpc(home)
    busy=[]
    s=DeletionService(home,rpc,guard=lambda pid:busy)
    plan=s.prepare({sid})
    busy.append('codex.exe')
    with pytest.raises(DeletionBlocked):
        s.execute(plan)
    assert not rpc.calls


def test_windows_extended_path_is_in_home(home):
    import os
    from session_manager.store import safe_rollout
    if os.name!='nt':
        pytest.skip('Windows extended-length paths')
    sid=add_session(home)
    with sqlite3.connect(home/'state_5.sqlite') as con:
        path=con.execute('SELECT rollout_path FROM threads WHERE id=?',(sid,)).fetchone()[0]
        con.execute('UPDATE threads SET rollout_path=? WHERE id=?',('\\\\?\\'+path,sid))
    assert safe_rollout(home,'\\\\?\\'+path)
    rpc=FakeRpc(home)
    s=service(home,rpc)
    result=s.execute(s.prepare({sid}))
    assert result.succeeded==[sid]
