import sqlite3
import pytest

from conftest import add_session
from test_deletion import FakeRpc, service
from session_manager.catalog import CatalogError, clean_deleted_catalog
from session_manager.store import inventory


def create_catalog(home, ids):
    path = home / 'sqlite' / 'codex-dev.db'
    path.parent.mkdir(exist_ok=True)
    with sqlite3.connect(path) as con:
        con.executescript('''
            CREATE TABLE local_thread_catalog(host_id TEXT, thread_id TEXT, display_title TEXT,
                PRIMARY KEY(host_id,thread_id));
            CREATE TABLE local_thread_catalog_hosts(host_id TEXT PRIMARY KEY,host_kind TEXT);
            INSERT INTO local_thread_catalog_hosts VALUES ('local','local'),('remote','ssh');
            CREATE TABLE local_thread_catalog_metadata(id INTEGER PRIMARY KEY,catalog_revision INTEGER);
            INSERT INTO local_thread_catalog_metadata VALUES(1,10);
        ''')
        con.executemany('INSERT INTO local_thread_catalog VALUES (?,?,?)',
                        [('local',sid,'test title') for sid in ids])
        if ids:
            con.execute('INSERT INTO local_thread_catalog VALUES (?,?,?)', ('remote',ids[0],'remote'))
    return path


def local_ids(path):
    with sqlite3.connect(path) as con:
        return {r[0] for r in con.execute("SELECT thread_id FROM local_thread_catalog WHERE host_id='local'")}


def test_cascade_cleans_desktop_without_touching_other_hosts_or_sessions(home):
    root = add_session(home)
    child = add_session(home,parent=root)
    keep = add_session(home)
    orphan = 'unselected-orphan'
    path = create_catalog(home,[root,child,keep,orphan])
    deletion = service(home,FakeRpc(home))
    result = deletion.execute(deletion.prepare({root}))
    assert set(result.succeeded)=={root,child} and not result.failures
    # Reopen the DB as a restarted desktop would.
    assert local_ids(path)=={keep,orphan}
    with sqlite3.connect(path) as con:
        assert con.execute("SELECT thread_id FROM local_thread_catalog WHERE host_id='remote'").fetchone()==(root,)
        assert con.execute('SELECT catalog_revision FROM local_thread_catalog_metadata').fetchone()[0]>10


def test_failed_api_deletion_keeps_title(home):
    keep,remove = add_session(home),add_session(home)
    path = create_catalog(home,[keep,remove])
    rpc = FakeRpc(home)
    rpc.fail.add(keep)
    deletion = service(home,rpc)
    result = deletion.execute(deletion.prepare({keep,remove}))
    assert result.succeeded==[remove] and keep in result.failures
    assert local_ids(path)=={keep}


def test_unknown_schema_blocks_before_content_deleted(home):
    sid = add_session(home)
    path = create_catalog(home,[sid])
    with sqlite3.connect(path) as con:
        con.execute('DROP TABLE local_thread_catalog_metadata')
    rpc = FakeRpc(home)
    with pytest.raises(CatalogError):
        service(home,rpc).prepare({sid})
    assert not rpc.calls and sid in inventory(home).sessions


def test_restored_content_preserves_catalog(home):
    sid = add_session(home)
    path = create_catalog(home,[sid])
    with pytest.raises(CatalogError):
        clean_deleted_catalog(home,{sid},inventory,lambda:None)
    assert local_ids(path)=={sid}


def test_catalog_failure_rolls_back_and_is_reported(home):
    a,b = add_session(home),add_session(home)
    path = create_catalog(home,[a,b])
    with sqlite3.connect(path) as con:
        con.execute("CREATE TRIGGER fail_revision BEFORE UPDATE ON local_thread_catalog_metadata BEGIN SELECT RAISE(ABORT, 'test failure'); END")
    deletion = service(home,FakeRpc(home))
    result = deletion.execute(deletion.prepare({a}))
    assert not result.succeeded and a in result.failures
    assert '桌面目录' in result.failures[a]
    assert a not in inventory(home).sessions
    assert local_ids(path)=={a,b}


def test_wrong_host_kind_blocks_deletion(home):
    sid = add_session(home)
    path = create_catalog(home,[sid])
    with sqlite3.connect(path) as con:
        con.execute("UPDATE local_thread_catalog_hosts SET host_kind='ssh' WHERE host_id='local'")
    rpc = FakeRpc(home)
    with pytest.raises(CatalogError):
        service(home,rpc).prepare({sid})
    assert not rpc.calls


def test_catalog_absent_does_not_create_database(home):
    sid = add_session(home)
    deletion = service(home,FakeRpc(home))
    assert deletion.execute(deletion.prepare({sid})).succeeded==[sid]
    assert not (home/'sqlite').exists()
