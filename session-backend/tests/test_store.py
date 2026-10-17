import json
import sqlite3
from pathlib import Path

import pytest

from session_manager.store import inventory, PreviewReader
from conftest import add_session


def test_all_storage_sources_merge(home):
    root = add_session(home, "普通会话")
    child = add_session(home, "子会话", parent=root, archived=True)
    paginated = add_session(home, "无文件的新版会话", mode="paginated", file=False)
    missing = add_session(home, "内容已缺失", file=False)
    inv = inventory(home, [{"id": root, "name": "API名字", "status": {"type": "notLoaded"}}])
    assert len(inv.sessions) == 4
    assert inv.db_count == 4 and inv.file_count == 2 and inv.api_count == 1
    assert inv.sessions[paginated].has_history
    assert inv.sessions[missing].status == "内容缺失"
    assert inv.sessions[child].archived and inv.sessions[child].is_child
    assert inv.affected({root, child}) == {root, child}
    assert not inv.warnings


def test_legacy_pagination_and_no_duplicate_event_messages(home):
    sid = add_session(home, count=125)
    reader = PreviewReader(home)
    s = inventory(home).sessions[sid]
    messages, cursor = [], None
    while True:
        page = reader.page(s, cursor)
        messages.extend(page.messages)
        cursor = page.cursor
        if cursor is None:
            break
    assert len(messages) == 125
    assert len({m.text for m in messages}) == 125
    assert messages[0].role == "user" and messages[1].role == "assistant"


@pytest.mark.parametrize('journal', [False, True])
def test_compressed_unicode_history_pagination(home, journal):
    import zstandard
    sid = add_session(home, '中文与 emoji 🧩 分页', count=125)
    path = Path(next(iter(inventory(home).sessions[sid].paths)))
    if journal:
        events = [{'type': 'session_meta', 'payload': {'id': sid, 'session_id': sid}}]
        for i in range(125):
            events.append({'type': 'event_msg', 'payload': {'type': 'item_completed', 'item': {
                'type': 'agentMessage', 'id': str(i), 'text': f'中文 🧩 内容 {i}'}}})
        path.write_text('\n'.join(json.dumps(e, ensure_ascii=False) for e in events), encoding='utf-8')
    compressed = path.with_suffix(path.suffix+'.zst')
    compressed.write_bytes(zstandard.ZstdCompressor().compress(path.read_bytes()))
    path.unlink()
    with sqlite3.connect(home/'state_5.sqlite') as con:
        con.execute('UPDATE threads SET rollout_path=? WHERE id=?', (str(compressed), sid))
    session = inventory(home).sessions[sid]
    reader, messages, cursor = PreviewReader(home), [], None
    for _ in range(10):
        page = reader.page(session, cursor)
        messages.extend(page.messages)
        cursor = page.cursor
        if cursor is None:
            break
    assert cursor is None and len(messages) == 125
    assert len({m.text for m in messages}) == 125
    assert all('中文' in m.text and '🧩' in m.text for m in messages)


def test_database_fallback_pagination(home):
    sid = add_session(home, mode="paginated", file=False, count=125)
    class FailingRpc:
        def call(self, *args, **kwargs):
            raise RuntimeError("missing source rollout")
    reader = PreviewReader(home, FailingRpc())
    s = inventory(home).sessions[sid]
    a = reader.page(s)
    b = reader.page(s, a.cursor)
    c = reader.page(s, b.cursor)
    assert [len(p.messages) for p in (a,b,c)] == [60,60,5]
    assert c.cursor is None
    assert a.backend == "本地数据库"


def test_corrupt_line_and_missing_content(home):
    sid = add_session(home)
    inv = inventory(home)
    path = next(iter(inv.sessions[sid].paths))
    with open(path,"a",encoding="utf-8") as f:
        f.write("{broken json}\n")
    page = PreviewReader(home).page(inv.sessions[sid])
    assert len(page.messages) == 4 and "损坏" in page.notice
    missing = add_session(home, file=False)
    page = PreviewReader(home).page(inventory(home).sessions[missing])
    assert "缺失" in page.notice


def test_unknown_file_record_is_kept(home):
    sid = add_session(home)
    with sqlite3.connect(home / "state_5.sqlite") as con:
        con.execute("DELETE FROM threads WHERE id=?",(sid,))
    inv = inventory(home)
    assert sid in inv.sessions and inv.db_count == 0


def test_readers_do_not_modify_database(home):
    sid = add_session(home,mode="paginated",file=False)
    before = (home / "state_5.sqlite").read_bytes()
    inv = inventory(home)
    PreviewReader(home).page(inv.sessions[sid])
    assert (home / "state_5.sqlite").read_bytes() == before


def test_official_item_envelope_is_decoded(home):
    sid=add_session(home)
    class Rpc:
        def call(self,*args,**kwargs):
            return {'data':[{'turnId':'turn','item':{'type':'agentMessage','text':'接口中的中文回答'}}], 'nextCursor':None}
    page=PreviewReader(home,Rpc()).page(inventory(home).sessions[sid])
    assert page.messages[0].text=='接口中的中文回答'
    assert page.backend=='官方接口'


def test_journal_extends_stale_database_without_duplicate_messages(home):
    from pathlib import Path
    sid = add_session(home, mode='paginated', count=4)
    s = inventory(home).sessions[sid]
    path = Path(next(iter(s.paths)))
    events = [{'type': 'session_meta', 'payload': {'id': sid, 'session_id': sid}}]
    # One projected message repeated in the journal, plus a recent user/AI pair.
    for item in [
        {'type': 'AgentMessage', 'id': '3', 'content': [{'type': 'Text', 'text': '回答 3'}]},
        {'type': 'UserMessage', 'id': 'new-user', 'content': [{'type': 'text', 'text': '继续'}]},
        {'type': 'AgentMessage', 'id': 'new-ai', 'content': [{'type': 'Text', 'text': '已完成最新功能。'}]},
    ]:
        events.append({'type': 'event_msg', 'payload': {'type': 'item_completed', 'item': item,
                       'started_at_ms': 1789365689635}})
        # Response mirrors must not create another copy of the conversation.
        events.append({'type': 'response_item', 'payload': {'type': 'message', 'role': 'assistant',
                       'content': [{'type': 'output_text', 'text': 'mirror'}]}})
    path.write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8')
    reader = PreviewReader(home, include_tools=False)
    reader.PAGE_SIZE = 2
    messages, cursor = [], None
    for _ in range(20):
        page = reader.page(s, cursor)
        messages.extend(page.messages)
        cursor = page.cursor
        if cursor is None:
            break
    assert cursor is None
    assert [m.text for m in messages] == ['问题 0', '回答 1', '问题 2', '回答 3', '继续', '已完成最新功能。']
    assert messages[-1].time == 1789365689.635


def test_dialogue_mode_skips_tool_only_api_pages(home):
    sid = add_session(home)
    class Rpc:
        def call(self, method, params, **kwargs):
            if params['cursor'] is None:
                return {'data': [{'type': 'commandExecution', 'command': 'test', 'output': {'text': '结果\n成功'}}], 'nextCursor': 'next'}
            return {'data': [{'type': 'agentMessage', 'text': '任务完成', 'createdAt': 1789365689}], 'nextCursor': None}
    page = PreviewReader(home, Rpc(), include_tools=False).page(inventory(home).sessions[sid])
    assert [m.text for m in page.messages] == ['任务完成']
    assert page.messages[0].time == 1789365689


def test_tool_output_is_readable_text():
    from session_manager.store import decode_item
    message = decode_item({'type': 'CommandExecution', 'command': 'example', 'output': {'text': '第一行\n第二行'}})
    assert message.kind == '执行命令'
    assert '第一行\n第二行' in message.text and '"type"' not in message.text
