"""Private JSON-lines bridge for Relay Lite's integrated session page.

Storage, deletion and ZIP compatibility come from CodexSessionManager 1.1.1.
The bridge never launches model turns and exposes no arbitrary RPC or SQL.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
from pathlib import Path
import secrets
import sys
import time

from session_manager.deletion import DeletionService, external_codex_clients
from session_manager.rpc import CodexRpc, discover_codex
from session_manager.store import InventoryReader, PreviewReader, canonical_path
from session_manager.transfer import export_bundle, inspect_bundle, ImportService


def session_row(session):
    return {key: getattr(session, key) for key in
            ('id', 'title', 'cwd', 'updated', 'created', 'archived', 'parent',
             'is_child', 'status', 'mode')} | {'origins': sorted(session.origins)}


class SessionService:
    def __init__(self, home, executable='', *, rpc_factory=CodexRpc,
                 guard=external_codex_clients):
        self.home = canonical_path(home)
        self.executable = executable or discover_codex()
        self.rpc = rpc_factory(self.executable, self.home) if self.executable else None
        self.guard = guard
        self.reader = InventoryReader()
        self.current = None
        self.tokens = {}

    def status(self):
        return {'home': str(self.home), 'executable': self.executable,
                'available': bool(self.executable), 'engineVersion': '1.1.1'}

    def _remember(self, kind, value):
        now = time.monotonic()
        self.tokens = {k: v for k, v in self.tokens.items() if now-v[0] < 600}
        while len(self.tokens) >= 256:
            self.tokens.pop(next(iter(self.tokens)))
        token = secrets.token_urlsafe(24)
        self.tokens[token] = (now, kind, value)
        return token

    def _token(self, token, kind, *, consume=False):
        entry = self.tokens.get(str(token))
        if not entry or entry[1] != kind or time.monotonic()-entry[0] >= 600:
            raise ValueError('操作已过期，请刷新或重新选择后重试。')
        if consume:
            del self.tokens[str(token)]
        return entry[2]

    def list_sessions(self):
        api_rows, warning = None, ''
        if self.rpc:
            try:
                api_rows = self.rpc.list_threads()
            except Exception as exc:
                warning = '官方接口暂不可用，当前显示本地记录：'+str(exc)
        inv = self.reader(self.home, api_rows)
        if warning:
            inv.warnings.append(warning)
        self.current = inv
        rows = sorted(inv.sessions.values(), key=lambda s: (s.updated, s.id), reverse=True)
        return {'sessions': [session_row(s) for s in rows], 'warnings': inv.warnings,
                'counts': {'total': len(rows), 'ordinary': sum(not s.archived for s in rows),
                           'archived': sum(s.archived for s in rows),
                           'database': inv.db_count, 'files': inv.file_count,
                           'official': inv.api_count}, **self.status()}

    def preview(self, args):
        sid, tools = str(args.get('id', '')), bool(args.get('tools', False))
        if not self.current:
            self.current = self.reader(self.home)
        session = self.current.sessions.get(sid)
        if not session:
            raise ValueError('会话已不存在，请刷新列表。')
        cursor, previous = None, None
        if args.get('cursor'):
            previous = self._token(args['cursor'], 'cursor')
            if previous['owner'] != sid or previous['tools'] != tools:
                raise ValueError('对话分页已变化，请重新选择会话。')
            if previous['result'] is not None:
                return previous['result']
            cursor = previous['cursor']
        page = PreviewReader(self.home, self.rpc, include_tools=tools).page(session, cursor)
        next_token = self._remember('cursor', {'owner': sid, 'tools': tools,
            'cursor': page.cursor, 'result': None}) if page.cursor is not None else None
        result = {'messages': [asdict(m) for m in page.messages], 'cursor': next_token,
                  'notice': page.notice, 'backend': page.backend}
        # Keep the current position and one replayable response. Older pages and
        # abandoned previews must not retain copies of an entire journal's IDs.
        if previous is not None:
            previous['cursor'], previous['result'] = None, result
        keep = {args.get('cursor'), next_token}
        for token, entry in list(self.tokens.items()):
            if entry[1] == 'cursor' and token not in keep:
                del self.tokens[token]
        return result

    def prepare_delete(self, ids):
        if not self.rpc:
            raise ValueError('未找到 codex.exe，请先在“读取设置”中选择。')
        service = DeletionService(self.home, self.rpc, guard=self.guard, reader=self.reader)
        plan = service.prepare(ids)
        rows = []
        for sid in sorted(plan.affected, key=lambda s: (s not in plan.selected, s)):
            session = plan.snapshot.sessions.get(sid)
            rows.append({'id': sid, 'title': session.title if session else '已缺失的子会话记录',
                         'selected': sid in plan.selected})
        return {'token': self._remember('delete', (service, plan)), 'sessions': rows,
                'selected': len(plan.selected), 'affected': len(plan.affected)}

    def prepare_import(self, filename):
        plan = inspect_bundle(self.home, Path(filename))
        return {'token': self._remember('import', plan), 'sessions': [
            {'id': r['id'], 'title': r['title'], 'exists': r['id'] in plan.existing}
            for r in plan.records], 'total': len(plan.records),
            'existing': len(plan.existing), 'importing': len(plan.records)-len(plan.existing)}

    def dispatch(self, action, args, progress=None):
        if action == 'status':
            return self.status()
        if not self.home.is_dir():
            raise ValueError('会话数据目录不存在，请在“读取设置”中选择 Codex 数据目录。')
        if action == 'list':
            return self.list_sessions()
        if action == 'preview':
            return self.preview(args)
        if action == 'prepareDelete':
            return self.prepare_delete(args.get('ids', []))
        if action == 'executeDelete':
            if args.get('confirmed') is not True:
                raise ValueError('请先核对完整会话列表并确认永久删除。')
            service, plan = self._token(args.get('token'), 'delete', consume=True)
            return asdict(service.execute(plan, progress))
        if action == 'export':
            count = export_bundle(self.home, set(args.get('ids', [])), Path(args['path']), progress)
            return {'count': count, 'path': args['path']}
        if action == 'prepareImport':
            return self.prepare_import(args['path'])
        if action == 'executeImport':
            if args.get('confirmed') is not True:
                raise ValueError('请先核对导入列表。')
            plan = self._token(args.get('token'), 'import', consume=True)
            if self.rpc:
                self.rpc.close()
            return asdict(ImportService(self.home, guard=lambda: self.guard()).execute(plan, progress))
        if action == 'cancelPlan':
            self.tokens.pop(str(args.get('token', '')), None)
            return True
        raise ValueError('不支持的会话操作。')

    def close(self):
        if self.rpc:
            self.rpc.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--home', required=True, type=Path)
    parser.add_argument('--codex', default='')
    args = parser.parse_args()
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='replace')
    service = None

    def emit(value):
        print(json.dumps(value, ensure_ascii=False, separators=(',', ':')), flush=True)

    try:
        service = SessionService(args.home, args.codex)
        for line in sys.stdin:
            request = {}
            try:
                request = json.loads(line)
                if request.get('action') == 'shutdown':
                    break
                def progress(done, total, label):
                    emit({'event': 'progress', 'done': done, 'total': total, 'label': str(label)[:180]})
                data = service.dispatch(request.get('action'), request.get('args') or {}, progress)
                emit({'id': request.get('id'), 'ok': True, 'data': data})
            except Exception as exc:
                emit({'id': request.get('id'), 'ok': False, 'error': str(exc) or type(exc).__name__})
    except Exception as exc:
        emit({'ok': False, 'error': str(exc)})
        return 1
    finally:
        if service:
            service.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
