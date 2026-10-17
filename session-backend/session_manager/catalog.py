"""Clean only confirmed deleted IDs from the desktop's persistent catalog."""
from contextlib import contextmanager
import sqlite3


class CatalogError(RuntimeError):
    pass


@contextmanager
def catalog_transaction(home):
    path = home / "sqlite" / "codex-dev.db"
    if not path.exists():
        yield None  # Older/CLI-only installations do not have this database.
        return
    if not path.resolve().is_relative_to(home.resolve()):
        raise CatalogError("桌面会话目录指向数据目录之外，已停止操作。")
    con = None
    try:
        con = sqlite3.connect(path.resolve().as_uri() + "?mode=rw", uri=True, timeout=5)
        con.execute("BEGIN IMMEDIATE")
        tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "local_thread_catalog" not in tables:
            yield None
        else:
            required = {
                "local_thread_catalog": {"host_id", "thread_id"},
                "local_thread_catalog_hosts": {"host_id", "host_kind"},
                "local_thread_catalog_metadata": {"id", "catalog_revision"},
            }
            for table, columns in required.items():
                actual = {r[1] for r in con.execute(f'PRAGMA table_info("{table}")')}
                if not columns <= actual:
                    raise CatalogError("桌面会话目录格式不兼容，未执行清理，请更新软件。")
            kind = con.execute("SELECT host_kind FROM local_thread_catalog_hosts WHERE host_id='local'").fetchone()
            local_count = con.execute("SELECT count(*) FROM local_thread_catalog WHERE host_id='local'").fetchone()[0]
            if local_count and kind != ("local",):
                raise CatalogError("无法确认桌面目录中的本地主机，已停止操作。")
            if con.execute("SELECT catalog_revision FROM local_thread_catalog_metadata WHERE id=1").fetchone() is None:
                raise CatalogError("桌面会话目录版本记录缺失，已停止操作。")
            yield con
        con.commit()
    except sqlite3.Error as exc:
        raise CatalogError(f"桌面会话目录无法读取或写入：{exc}") from exc
    finally:
        if con is not None:
            con.close()  # Rolls back on every failure before commit.


def check_catalog(home):
    """Validate schema and writability before irreversible transcript deletion."""
    with catalog_transaction(home):
        pass


def clean_deleted_catalog(home, ids, reader, guard):
    ids = set(ids)
    if not ids:
        return
    guard()
    with catalog_transaction(home) as con:
        if con is None:
            return
        current = reader(home)
        if current.warnings:
            raise CatalogError("会话状态读取不完整，未清理桌面目录。")
        if ids.intersection(current.sessions):
            raise CatalogError("会话仍存在或已经恢复，未清理桌面目录。")
        guard()
        con.executemany("DELETE FROM local_thread_catalog WHERE host_id='local' AND thread_id=?",
                        [(sid,) for sid in sorted(ids)])
        con.execute("UPDATE local_thread_catalog_metadata SET catalog_revision=catalog_revision+1 WHERE id=1")
        for sid in ids:
            if con.execute("SELECT 1 FROM local_thread_catalog WHERE host_id='local' AND thread_id=?", (sid,)).fetchone():
                raise CatalogError("桌面会话目录清理未通过验证，已回滚。")
