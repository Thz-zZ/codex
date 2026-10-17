import sqlite3
from pathlib import Path

from conftest import add_session
from test_deletion import FakeRpc, service
from session_manager.store import InventoryReader, inventory


def test_focused_scan_keeps_selected_content_signatures(home):
    legacy=add_session(home)
    paginated=add_session(home,mode='paginated')
    add_session(home)
    reader=InventoryReader()
    full=reader(home)
    for sid in (legacy,paginated):
        focused=reader(home,focus={sid})
        assert focused.signature({sid})==full.signature({sid})
        assert not focused.warnings


def test_cached_scan_observes_changed_and_new_files(home):
    sid=add_session(home)
    reader=InventoryReader()
    before=reader(home)
    path=Path(next(iter(before.sessions[sid].paths)))
    with path.open('a',encoding='utf-8') as f:
        f.write('\n')
    child=add_session(home,parent=sid)
    with sqlite3.connect(home/'state_5.sqlite') as con:
        con.execute('DELETE FROM threads WHERE id=?',(child,))
        con.execute('DELETE FROM thread_spawn_edges WHERE child_thread_id=?',(child,))
    after=reader(home,focus={sid})
    assert after.signature({sid})!=before.signature({sid})
    assert after.affected({sid})=={sid,child}


def test_file_only_new_child_during_batch_stops_upcoming_root(home):
    a,b=sorted([add_session(home),add_session(home)])
    created=[]
    class NewChildRpc(FakeRpc):
        def call(self,method,params,**kwargs):
            result=super().call(method,params,**kwargs)
            if params['threadId']==a:
                child=add_session(home,parent=b)
                created.append(child)
                with sqlite3.connect(home/'state_5.sqlite') as con:
                    con.execute('DELETE FROM threads WHERE id=?',(child,))
                    con.execute('DELETE FROM thread_spawn_edges WHERE child_thread_id=?',(child,))
            return result
    rpc=NewChildRpc(home)
    deletion=service(home,rpc)
    result=deletion.execute(deletion.prepare({a,b}))
    assert rpc.calls==[a] and result.stopped
    assert result.succeeded==[a] and b in result.failures
    assert {b,created[0]}<=inventory(home).sessions.keys()


def test_history_only_remainder_is_not_reported_deleted(home):
    sid=add_session(home,mode='paginated')
    deletion=service(home,FakeRpc(home))
    result=deletion.execute(deletion.prepare({sid}))
    assert sid in result.failures and not result.succeeded
    assert inventory(home).sessions[sid].has_history
