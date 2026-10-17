import json
from pathlib import Path
import sqlite3
import sys
import time
import uuid

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

STATE_SCHEMA = """
CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT NOT NULL,created_at INTEGER NOT NULL,
updated_at INTEGER NOT NULL,source TEXT NOT NULL,model_provider TEXT NOT NULL,cwd TEXT NOT NULL,
title TEXT NOT NULL,sandbox_policy TEXT NOT NULL,approval_mode TEXT NOT NULL,
has_user_event INTEGER DEFAULT 1, archived INTEGER DEFAULT 0, history_mode TEXT DEFAULT 'legacy',
name TEXT,thread_source TEXT);
CREATE TABLE thread_spawn_edges(parent_thread_id TEXT,child_thread_id TEXT PRIMARY KEY,status TEXT);
"""
HISTORY_SCHEMA = """
CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_id TEXT,rollout_ordinal INTEGER,
created_at_ms INTEGER,item_json TEXT,item_type TEXT, PRIMARY KEY(thread_id,turn_id,item_id));
CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,rollout_ordinal INTEGER,status TEXT,
PRIMARY KEY(thread_id,turn_id));
"""


def add_session(home, title="测试会话", archived=False, mode="legacy", parent=None, file=True, count=4):
    sid = str(uuid.uuid4())
    folder = home / ("archived_sessions" if archived else "sessions/2026/09/14")
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"rollout-2026-09-14T12-00-00-{sid}.jsonl"
    source = {"subagent": {"thread_spawn": {"parent_thread_id": parent, "depth": 1}}} if parent else "cli"
    now = int(time.time())
    with sqlite3.connect(home / "state_5.sqlite") as con:
        con.execute("INSERT INTO threads (id,rollout_path,created_at,updated_at,source,model_provider,cwd,title,sandbox_policy,approval_mode,has_user_event,archived,history_mode) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    (sid, str(path), now, now, json.dumps(source) if parent else source, "openai", str(home / "project"), title, '{"type":"read-only"}', "never", 1, int(archived), mode))
        if parent:
            con.execute("INSERT INTO thread_spawn_edges VALUES (?,?,?)", (parent,sid,"completed"))
    events = [{"timestamp": "2026-09-14T04:00:00Z", "type": "session_meta", "payload": {
        "id": sid, "timestamp": "2026-09-14T04:00:00Z", "cwd": str(home / "project"), "originator": "codex_cli_rs", "cli_version": "0.154.0", "source": source, "model_provider": "openai"}}]
    for i in range(count):
        role = "user" if i % 2 == 0 else "assistant"
        events.append({"timestamp": "2026-09-14T04:00:01Z", "type": "response_item", "payload": {
            "type": "message", "role": role, "content": [{"type": "input_text" if role == "user" else "output_text", "text": f"{title}：第 {i+1} 条中文内容"}]}})
        events.append({"timestamp": "2026-09-14T04:00:01Z", "type": "event_msg", "payload": {
            "type": "user_message" if role == "user" else "agent_message", "message": f"{title}：第 {i+1} 条中文内容"}})
    if file:
        path.write_text("\n".join(json.dumps(e, ensure_ascii=False) for e in events) + "\n", encoding="utf-8")
    if mode == "paginated":
        history = home / "thread_history_1.sqlite"
        first = not history.exists()
        with sqlite3.connect(history) as con:
            if first:
                con.executescript(HISTORY_SCHEMA)
            for i in range(count):
                item = ({"type": "userMessage", "content": [{"type": "text", "text": f"问题 {i}"}]} if i % 2 == 0 else {"type": "agentMessage", "text": f"回答 {i}"})
                item["id"] = str(i)
                con.execute("INSERT INTO thread_items(thread_id,turn_id,item_id,rollout_ordinal,created_at_ms,item_json,item_type) VALUES (?,?,?,?,?,?,?)", (sid,"turn-1",str(i),i,now*1000,json.dumps(item),item["type"]))
    return sid


@pytest.fixture
def home(tmp_path):
    path = tmp_path / "codex-test"
    path.mkdir()
    with sqlite3.connect(path / "state_5.sqlite") as con:
        con.executescript(STATE_SCHEMA)
    return path
