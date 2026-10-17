"""Read-only inventory and transcript adapters for Codex local data."""
from __future__ import annotations

from dataclasses import dataclass, field
from contextlib import contextmanager
from datetime import datetime
import json
import io
import os
from pathlib import Path
import re
import sqlite3
from typing import Any

UUID_RE = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", re.I)


def default_home() -> Path:
    return Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex").resolve()


def canonical_path(value) -> Path:
    """Treat Windows extended-length and ordinary spellings as the same path."""
    text = str(Path(value).resolve())
    if text.startswith("\\\\?\\UNC\\"):
        text = "\\\\" + text[8:]
    elif text.startswith("\\\\?\\"):
        text = text[4:]
    return Path(text)


@contextmanager
def connect_readonly(path: Path):
    con = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True, timeout=3)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA query_only=ON")
    try:
        yield con
    finally:
        con.close()


def db_file(home: Path, prefix: str) -> Path | None:
    candidates = [p for p in home.glob(f"{prefix}_*.sqlite") if re.fullmatch(re.escape(prefix) + r"_\d+\.sqlite", p.name)]
    return max(candidates, key=lambda p: int(p.stem.rsplit("_", 1)[1])) if candidates else None


def parent_from_source(source: Any) -> str:
    try:
        value = json.loads(source) if isinstance(source, str) else source
        return value.get("subagent", {}).get("thread_spawn", {}).get("parent_thread_id", "") or ""
    except (ValueError, AttributeError, TypeError):
        return ""


def timestamp(value) -> float:
    if isinstance(value, (int, float)):
        return value / 1000 if value > 10**11 else float(value)
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00")).timestamp()
    except (ValueError, TypeError):
        return 0.0


@dataclass
class Session:
    id: str
    title: str = "未命名会话"
    cwd: str = ""
    updated: float = 0
    created: float = 0
    archived: bool = False
    parent: str = ""
    is_child: bool = False
    mode: str = "legacy"
    paths: set[str] = field(default_factory=set)
    origins: set[str] = field(default_factory=set)
    has_history: bool = False
    status: str = ""
    api_status: str = ""
    in_db: bool = False
    content_version: tuple = ()

    def identity(self):
        return (self.id, self.parent, self.mode, self.updated, self.archived, tuple(sorted(self.paths)), self.content_version)


@dataclass
class Inventory:
    sessions: dict[str, Session] = field(default_factory=dict)
    edges: set[tuple[str, str]] = field(default_factory=set)
    warnings: list[str] = field(default_factory=list)
    db_count: int = 0
    file_count: int = 0
    api_count: int = 0

    def affected(self, selected):
        result = set(selected)
        while True:
            added = {child for parent, child in self.edges if parent in result} - result
            if not added:
                return result
            result.update(added)

    def signature(self, ids):
        return tuple(sorted((self.sessions[i].identity() if i in self.sessions else (i,)) for i in ids))


def safe_rollout(home: Path, value: str) -> bool:
    if not value:
        return True
    path = canonical_path(value)
    return any(path.is_relative_to(canonical_path(home / folder)) for folder in ("sessions", "archived_sessions"))


def walk_rollouts(home: Path):
    for folder in ("sessions", "archived_sessions"):
        base = home / folder
        if not base.is_dir() or base.is_symlink() or (hasattr(base, "is_junction") and base.is_junction()):
            continue
        for root, dirs, files in os.walk(base, followlinks=False):
            dirs[:] = [d for d in dirs if not (Path(root) / d).is_symlink()
                       and not (hasattr(Path, "is_junction") and (Path(root) / d).is_junction())]
            for name in files:
                p = Path(root) / name
                if name.endswith((".jsonl", ".jsonl.zst")) and not p.is_symlink():
                    yield p, folder == "archived_sessions"


class _SeekableZstdReader(io.RawIOBase):
    """Reopen for backward seeks, keeping compressed transcript reads streaming."""
    def __init__(self, path):
        import zstandard
        self.path = path
        self.decoder = zstandard.ZstdDecompressor()
        self.stream = self.decoder.stream_reader(path.open('rb'), closefd=True)

    def readable(self):
        return True

    def seekable(self):
        return True

    def readinto(self, buffer):
        return self.stream.readinto(buffer)

    def tell(self):
        return self.stream.tell()

    def seek(self, offset, whence=io.SEEK_SET):
        if whence == io.SEEK_CUR:
            offset += self.tell()
        elif whence != io.SEEK_SET:
            raise io.UnsupportedOperation('Seek from compressed stream end is unsupported')
        if offset < 0:
            raise ValueError('Negative stream position')
        if offset < self.tell():
            self.stream.close()
            self.stream = self.decoder.stream_reader(self.path.open('rb'), closefd=True)
        return self.stream.seek(offset)

    def close(self):
        stream = getattr(self, 'stream', None)
        if not self.closed and stream is not None:
            stream.close()
        super().close()


def open_rollout(path: Path):
    if path.suffix == ".zst":
        # zstandard.open's text stream rejects even seek(0). TextIOWrapper needs
        # seekable byte positions to preserve UTF-8 decoder state across pages.
        return io.TextIOWrapper(_SeekableZstdReader(path), encoding="utf-8", errors="replace")
    return path.open("r", encoding="utf-8", errors="replace")


def rollout_meta(path: Path):
    with open_rollout(path) as f:
        # Session metadata is at the beginning, never scan a whole transcript for the list.
        for _ in range(8):
            line = f.readline(1024 * 1024)
            if not line:
                break
            try:
                event = json.loads(line)
            except ValueError:
                continue
            if event.get("type") == "session_meta":
                return event.get("payload", {})
    return {}


class InventoryReader:
    """Cache unchanged file headers, never directory listings or DB contents."""
    def __init__(self):
        self.headers = {}

    def __call__(self, home, api_rows=None, *, focus=None):
        return inventory(home, api_rows, focus=focus, headers=self.headers)


def inventory(home: Path, api_rows=None, *, focus=None, headers=None) -> Inventory:
    # A focused scan still discovers every file's identity and parent relation;
    # only expensive content signatures are restricted to the selected subtree.
    # Public/UI callers use full scans. No SQLite rows or file lists are cached.
    focus = set(focus) if focus is not None else None
    seen_headers = set()
    result = Inventory()
    home = home.resolve()
    if not home.is_dir():
        raise ValueError("数据目录不存在，请在设置中选择 Codex 数据目录。")
    state = db_file(home, "state")
    if state:
        try:
            with connect_readonly(state) as con:
                for raw in con.execute("SELECT * FROM threads"):
                    row = dict(raw)
                    sid = row["id"]
                    source = row.get("source", "")
                    s = Session(sid, title=row.get("name") or row.get("title") or row.get("first_user_message") or "未命名会话",
                                cwd=row.get("cwd", ""), updated=timestamp(row.get("updated_at_ms") or row.get("updated_at")),
                                created=timestamp(row.get("created_at_ms") or row.get("created_at")),
                                archived=bool(row.get("archived")), parent=parent_from_source(source),
                                is_child=bool(parent_from_source(source)) or row.get("thread_source") in ("subagent", "guardian_review"),
                                mode=row.get("history_mode", "legacy"), in_db=True, origins={"数据库"})
                    if row.get("rollout_path") and (focus is None or sid in focus):
                        s.paths.add(str(canonical_path(row["rollout_path"])))
                    result.sessions[sid] = s
                result.db_count = len(result.sessions)
                tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "thread_spawn_edges" in tables:
                    result.edges.update((r[0], r[1]) for r in con.execute("SELECT parent_thread_id, child_thread_id FROM thread_spawn_edges"))
        except (sqlite3.Error, OSError, KeyError) as exc:
            result.warnings.append(f"会话索引读取不完整：{exc}")
    history = db_file(home, "thread_history")
    if history:
        try:
            with connect_readonly(history) as con:
                where, params = "", ()
                if focus is not None and 0 < len(focus) <= 900:
                    where = " WHERE thread_id IN (" + ",".join("?" for _ in focus) + ")"
                    params = tuple(sorted(focus))
                for r in con.execute("SELECT thread_id,count(*),max(rollout_ordinal),max(created_at_ms) FROM thread_items" + where + " GROUP BY thread_id", params):
                    sid = r[0]
                    s = result.sessions.setdefault(sid, Session(sid, mode="paginated", origins={"历史数据库"}))
                    s.has_history = True
                    s.content_version = (tuple(r[1:]),)
                tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                for table in ("thread_turns", "thread_realtime_items"):
                    if table in tables:
                        for r in con.execute(f"SELECT thread_id,count(*) FROM {table}" + where + " GROUP BY thread_id", params):
                            s = result.sessions.setdefault(r[0], Session(r[0], mode="paginated", origins={"历史数据库"}))
                            s.has_history = True
                            s.content_version += ((table, r[1]),)
        except (sqlite3.Error, OSError) as exc:
            result.warnings.append(f"对话内容索引读取失败：{exc}")
    try:
        for path, archived in walk_rollouts(home):
            result.file_count += 1
            try:
                if headers is None:
                    meta = rollout_meta(path)
                else:
                    # Replacement, rename, edits and newly created files all
                    # invalidate cached metadata. Stat is fresh on every scan.
                    stat = path.stat()
                    version = (stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)
                    key = str(path)
                    seen_headers.add(key)
                    cached = headers.get(key)
                    if cached is not None and cached[0] == version:
                        meta = cached[1]
                    else:
                        meta = rollout_meta(path)
                        headers[key] = (version, meta)
                match = UUID_RE.search(path.name)
                sid = meta.get("id") or (match.group(0) if match else "")
                if not sid:
                    result.warnings.append(f"无法识别会话文件：{path.name}")
                    continue
                s = result.sessions.get(sid)
                if s is None:
                    s = Session(sid, title=meta.get("name") or meta.get("title") or f"会话 {sid[:8]}",
                                cwd=meta.get("cwd", ""), updated=path.stat().st_mtime,
                                created=timestamp(meta.get("timestamp")), archived=archived,
                                parent=parent_from_source(meta.get("source")))
                    result.sessions[sid] = s
                if focus is None or sid in focus:
                    s.paths.add(str(canonical_path(path)))
                s.origins.add("会话文件")
                s.parent = s.parent or parent_from_source(meta.get("source"))
            except (OSError, ValueError) as exc:
                result.warnings.append(f"文件无法读取：{path.name}（{exc}）")
    except OSError as exc:
        result.warnings.append(f"会话文件扫描失败：{exc}")
    if headers is not None:
        for key in headers.keys() - seen_headers:
            del headers[key]
    # Index titles may contain user-renamed names missing from old rollout metadata.
    index = home / "session_index.jsonl"
    if index.is_file():
        try:
            with index.open(encoding="utf-8", errors="replace") as f:
                for line in f:
                    try:
                        row = json.loads(line)
                        s = result.sessions.get(row.get("id"))
                        if s and not s.in_db and row.get("thread_name"):
                            s.title = row["thread_name"]
                    except (ValueError, AttributeError):
                        continue
        except OSError as exc:
            result.warnings.append(f"标题索引读取失败：{exc}")
    for row in api_rows or []:
        sid = row.get("id")
        if not sid or row.get("ephemeral"):
            continue
        result.api_count += 1
        s = result.sessions.setdefault(sid, Session(sid))
        s.origins.add("官方接口")
        if not s.in_db:
            s.title = row.get("name") or row.get("preview") or s.title
            s.cwd = row.get("cwd") or s.cwd
            s.updated = timestamp(row.get("updatedAt")) or s.updated
            s.archived = bool(row.get("archived"))
            s.mode = row.get("historyMode") or s.mode
        s.parent = s.parent or row.get("parentThreadId") or parent_from_source(row.get("source"))
        s.api_status = (row.get("status") or {}).get("type", "")
        if row.get("path"):
            s.paths.add(str(canonical_path(row["path"])))
    for s in result.sessions.values():
        if s.parent:
            result.edges.add((s.parent, s.id))
            s.is_child = True
        file_versions = []
        for p in sorted(s.paths):
            try:
                stat = Path(p).stat()
                file_versions.append((p, stat.st_size, stat.st_mtime_ns))
            except FileNotFoundError:
                continue
            except OSError as exc:
                result.warnings.append(f"无法检查会话文件：{Path(p).name}（{exc}）")
        exists = bool(file_versions)
        s.content_version += tuple(file_versions)
        if not exists and not s.has_history:
            s.status = "内容缺失" if "官方接口" not in s.origins else "待载入"
        elif s.mode not in ("legacy", "paginated"):
            s.status = "未知存储格式"
    for parent, child in result.edges:
        if child in result.sessions:
            result.sessions[child].parent = parent
            result.sessions[child].is_child = True
    return result


@dataclass
class Message:
    role: str
    text: str
    time: float = 0
    kind: str = ""


@dataclass
class PreviewPage:
    messages: list[Message]
    cursor: Any = None
    backend: str = ""
    notice: str = ""


def text_content(content):
    if isinstance(content, str):
        return content
    if isinstance(content, dict):
        content = [content]
    pieces = []
    for part in content or []:
        if isinstance(part, str):
            pieces.append(part)
        elif isinstance(part, dict):
            kind = part.get("type", "")
            if "text" in part:
                pieces.append(str(part["text"]))
            elif kind in ("image", "input_image", "localImage", "local_image", "LocalImage", "Image"):
                pieces.append("[图片附件]")
            elif kind:
                pieces.append(f"[{kind}]")
    return "\n".join(pieces)


def decode_item(item: dict, at=0) -> Message | None:
    kind = item.get("type", "")
    # Journal items use Rust variant names; API/database items use camelCase.
    if kind:
        kind = kind[0].lower() + kind[1:]
    at = at or timestamp(item.get("createdAt") or item.get("created_at_ms"))
    if kind in ("userMessage", "user_message"):
        content = text_content(item.get("content") or item.get("message", ""))
        # Desktop attachment prompts wrap the actual question in file metadata.
        if content.lstrip().startswith("# Files mentioned by the user:") and "## My request:" in content:
            content = content.split("## My request:", 1)[1].strip()
        return Message("user", content, at, kind)
    if kind in ("agentMessage", "agent_message"):
        return Message("assistant", str(item.get("text") or item.get("message") or text_content(item.get("content"))), at, kind)
    if kind == "message":
        role = item.get("role")
        if role in ("user", "assistant"):
            return Message(role, text_content(item.get("content")), at, kind)
        return None
    if kind in ("reasoning", "plan"):
        return None
    if kind in ("commandExecution", "function_call", "function_call_output", "custom_tool_call", "custom_tool_call_output", "mcpToolCall", "dynamicToolCall", "fileChange", "collabAgentToolCall", "imageView", "webSearch", "contextCompaction", "subAgentActivity", "sleep"):
        # Plain text only; no external image/link loading or executable rich content.
        labels = {"commandExecution": "执行命令", "fileChange": "修改文件", "webSearch": "搜索网页",
                  "mcpToolCall": "调用工具", "dynamicToolCall": "调用工具", "function_call": "调用工具",
                  "function_call_output": "工具结果", "custom_tool_call": "调用工具",
                  "custom_tool_call_output": "工具结果", "imageView": "查看图片", "sleep": "等待"}
        parts = []
        if item.get("command"):
            parts.append("命令：\n" + str(item["command"]))
        if item.get("name") or item.get("tool"):
            parts.append("工具：" + str(item.get("name") or item.get("tool")))
        output = item.get("output", item.get("aggregatedOutput"))
        if output is not None:
            if isinstance(output, dict) and "text" in output:
                output = output["text"]
            parts.append("结果：\n" + (output if isinstance(output, str) else json.dumps(output, ensure_ascii=False, indent=2)))
        return Message("tool", "\n\n".join(parts) or json.dumps(item, ensure_ascii=False, indent=2), at, labels.get(kind, "工具活动"))
    return None


class PreviewReader:
    PAGE_SIZE = 60

    def __init__(self, home: Path, rpc=None, include_tools=True):
        self.home, self.rpc = home, rpc
        self.include_tools = include_tools

    def page(self, session: Session, cursor=None) -> PreviewPage:
        # Advance over tool-only pages without making the user open each one.
        messages, notices = [], []
        for _ in range(20):
            page = self._page(session, cursor)
            messages.extend(m for m in page.messages if self.include_tools or m.role != "tool")
            if page.notice:
                notices.append(page.notice)
            cursor = page.cursor
            if messages or cursor is None:
                break
        return PreviewPage(messages, cursor, page.backend, "；".join(dict.fromkeys(notices)))

    def _page(self, session: Session, cursor=None) -> PreviewPage:
        backend = cursor.get("backend") if cursor else None
        if backend == "journal":
            return self._journal(session, cursor)
        if backend is None:
            journals = []
            for path in sorted(session.paths):
                if Path(path).is_file() and rollout_meta(Path(path)).get("session_id"):
                    journals.append(path)
            if journals:
                # The current journal can extend beyond the official API's DB
                # projection. Read the prefix and journal as one paged transcript.
                ids = set()
                history = db_file(self.home, "thread_history")
                if history:
                    with connect_readonly(history) as con:
                        ids = {r[0] for r in con.execute("SELECT item_id FROM thread_items WHERE thread_id=?", (session.id,))}
                return self._journal(session, {"backend": "journal", "paths": journals,
                    "stage": "db" if ids else "files", "db_cursor": None,
                    "index": 0, "file_cursor": None, "seen": ids})
        # Try the official paginated API first. Legacy files are streamed rather than
        # asking thread/read to hydrate an unbounded whole conversation.
        if self.rpc and backend in (None, "api"):
            try:
                result = self.rpc.call("thread/items/list", {
                    "threadId": session.id, "limit": self.PAGE_SIZE,
                    "sortDirection": "asc", "cursor": cursor.get("value") if cursor else None}, timeout=8)
                raw_items = result.get("data", result.get("items", []))
                msgs = [m for item in raw_items if (m := decode_item(item.get("item", item), timestamp(item.get("createdAt") or item.get("created_at_ms"))))]
                next_cursor = result.get("nextCursor")
                if raw_items or next_cursor or backend == "api":
                    return PreviewPage(msgs, {"backend": "api", "value": next_cursor} if next_cursor else None, "官方接口")
            except Exception:
                if backend == "api":
                    raise RuntimeError("对话分页连接中断，请重新选择会话以载入，避免内容重复。")
        if session.has_history or backend == "db":
            return self._db(session, cursor if backend == "db" else None)
        paths = sorted(p for p in session.paths if Path(p).is_file())
        if paths:
            return self._jsonl(Path(paths[0]), cursor if backend == "jsonl" else None)
        return PreviewPage([], notice="此会话的本地对话内容已缺失，仅保留索引记录。")

    def _journal(self, session, cursor):
        state = dict(cursor)
        if state["stage"] == "db":
            page = self._db(session, state["db_cursor"])
            state["db_cursor"] = page.cursor
            if page.cursor is None:
                state["stage"] = "files"
            return PreviewPage(page.messages, state, "对话记录", page.notice)
        path = Path(state["paths"][state["index"]])
        seen, added = state["seen"], set()
        inner = state["file_cursor"] or {"offset": 0, "strategy": "completed"}
        messages, bad, count = [], 0, 0
        with open_rollout(path) as f:
            f.seek(inner["offset"])
            while len(messages) < self.PAGE_SIZE and count < 2000:
                line = f.readline()
                if not line:
                    break
                count += 1
                try:
                    event = json.loads(line)
                    payload = event.get("payload", {})
                    if event.get("type") != "event_msg" or payload.get("type") != "item_completed":
                        continue
                    item = payload.get("item", {})
                    ident = item.get("id")
                    if ident and (ident in seen or ident in added):
                        continue
                    if ident:
                        added.add(ident)
                    message = decode_item(item, timestamp(payload.get("started_at_ms") or event.get("timestamp")))
                    if message:
                        messages.append(message)
                except (ValueError, AttributeError, TypeError):
                    bad += 1
            position = f.tell()
            more = bool(f.read(1))
        # Commit only after a successful read, so an I/O failure can retry the
        # same cursor. The active preview shares one set across all its pages.
        seen.update(added)
        state["file_cursor"] = {"offset": position}
        if not more:
            state["index"] += 1
            state["file_cursor"] = None
        return PreviewPage(messages, state if more or state["index"] < len(state["paths"]) else None,
                           "对话记录", f"有 {bad} 条记录无法解析。" if bad else "")

    def _db(self, session, cursor):
        path = db_file(self.home, "thread_history")
        if not path:
            return PreviewPage([], notice="未找到对话内容数据库。")
        offset = cursor.get("offset", 0) if cursor else 0
        with connect_readonly(path) as con:
            rows = con.execute("SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=? ORDER BY rollout_ordinal,item_id LIMIT ? OFFSET ?",
                               (session.id, self.PAGE_SIZE + 1, offset)).fetchall()
        msgs, corrupt = [], 0
        for row in rows[:self.PAGE_SIZE]:
            try:
                item = json.loads(row[0])
                m = decode_item(item, timestamp(row[1]))
                if m:
                    msgs.append(m)
            except (ValueError, TypeError, AttributeError):
                corrupt += 1
        return PreviewPage(msgs, {"backend": "db", "offset": offset + self.PAGE_SIZE} if len(rows) > self.PAGE_SIZE else None,
                           "本地数据库", f"有 {corrupt} 条记录无法解析。" if corrupt else "")

    def _jsonl(self, path, cursor):
        offset = cursor.get("offset", 0) if cursor else 0
        strategy = cursor.get("strategy") if cursor else None
        if not strategy:
            # Old rollouts often contain both response_item and event_msg copies.
            strategy = "events"
            with open_rollout(path) as probe:
                for line in probe:
                    if '"response_item"' in line:
                        strategy = "responses"
                        break
        messages, bad, count = [], 0, 0
        with open_rollout(path) as f:
            f.seek(offset)
            while len(messages) < self.PAGE_SIZE and count < 2000:
                line = f.readline()
                if not line:
                    break
                count += 1
                try:
                    event = json.loads(line)
                    payload = event.get("payload", {})
                    kind = event.get("type")
                    accepted = (strategy == "responses" and kind == "response_item") or (strategy == "events" and kind == "event_msg")
                    if accepted and isinstance(payload, dict):
                        m = decode_item(payload, timestamp(event.get("timestamp")))
                        if m:
                            messages.append(m)
                except (ValueError, AttributeError):
                    bad += 1
            position = f.tell()
            more = bool(f.read(1))
        return PreviewPage(messages, {"backend": "jsonl", "offset": position, "strategy": strategy} if more else None,
                           "本地会话文件", f"跳过 {bad} 行损坏记录。" if bad else "")
