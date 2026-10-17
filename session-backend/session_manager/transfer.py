"""Version-checked native session bundles. Never import executable SQL or config."""
from __future__ import annotations

from contextlib import ExitStack
from dataclasses import dataclass, field
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import tempfile
import uuid
import zipfile

from .deletion import external_codex_clients
from .store import canonical_path, connect_readonly, db_file, inventory, safe_rollout, rollout_meta, UUID_RE

FORMAT = 'codex-session-manager-bundle'
VERSION = 1
TABLES = {
    'state': ('threads', 'thread_dynamic_tools', 'thread_artifacts'),
    'thread_history': ('thread_turns', 'thread_items', 'thread_realtime_items', 'thread_history_projection_state'),
}
MAX_MEMBER = 512 * 1024 * 1024
MAX_TOTAL = 4 * 1024 * 1024 * 1024
MAX_JSON = 128 * 1024 * 1024


@dataclass
class ImportPlan:
    path: Path
    digest: str
    manifest: dict
    records: list[dict]
    existing: set[str]


@dataclass
class TransferResult:
    succeeded: list[str] = field(default_factory=list)
    skipped: list[str] = field(default_factory=list)
    failures: dict[str, str] = field(default_factory=dict)


def _hash(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def _json(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')


def _schema(con, table, database='main'):
    return [list(row) for row in con.execute(f'PRAGMA {database}.table_info("{table}")')]


def _rows(con, table, sid):
    key = 'id' if table == 'threads' else 'thread_id'
    return [dict(r) for r in con.execute(f'SELECT * FROM "{table}" WHERE "{key}"=?', (sid,))]


def export_bundle(home: Path, selected, destination: Path, progress=None):
    home, destination = home.resolve(), destination.resolve()
    if destination.suffix.lower() != '.zip':
        raise ValueError('导出文件必须使用 .zip 扩展名。')
    before = inventory(home)
    selected = sorted(set(selected))
    if not selected or set(selected) - before.sessions.keys():
        raise ValueError('请刷新后选择要导出的会话。')
    if before.warnings:
        raise ValueError('数据读取不完整，未导出：' + '；'.join(before.warnings))
    storage_owners = {}
    for session in before.sessions.values():
        for p in session.paths:
            ids = UUID_RE.findall(Path(p).name)
            if ids and Path(p).is_file():
                storage_owners[ids[-1]] = session.id
    for sid in selected:
        for p in before.sessions[sid].paths:
            if Path(p).is_file():
                base = rollout_meta(Path(p)).get('history_base') or {}
                source = base.get('thread_id')
                if source:
                    owner = storage_owners.get(source)
                    if owner not in selected:
                        title = before.sessions[owner].title if owner else source
                        raise ValueError('会话依赖未选中的历史源文件，请一并选择源会话后导出：' + title)
    manifest = {'format': FORMAT, 'version': VERSION,
                'created_at': datetime.now(timezone.utc).isoformat(),
                'schemas': {}, 'sessions': [], 'members': {},
                'edges': [list(e) for e in before.edges if e[0] in selected and e[1] in selected]}
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix='.codex-export-', suffix='.tmp', dir=destination.parent)
    os.close(fd)
    total = 0
    try:
        with ExitStack() as stack, zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
            connections = {}
            for prefix, tables in TABLES.items():
                path = db_file(home, prefix)
                if path:
                    con = stack.enter_context(connect_readonly(path))
                    con.execute('BEGIN')
                    connections[prefix] = con
                    manifest['schemas'][prefix] = {t: _schema(con, t) for t in tables if _schema(con, t)}
            for i, sid in enumerate(selected):
                session = before.sessions[sid]
                if progress:
                    progress(i, len(selected), session.title)
                record = {'id': sid, 'title': session.title, 'cwd': session.cwd,
                          'archived': session.archived, 'files': [], 'tables': {}}
                for prefix, con in connections.items():
                    record['tables'][prefix] = {t: _rows(con, t, sid) for t in manifest['schemas'][prefix]}
                state_rows = record['tables'].get('state', {}).get('threads', [])
                primary = canonical_path(state_rows[0]['rollout_path']) if state_rows and state_rows[0].get('rollout_path') else None
                for n, source in enumerate(sorted(session.paths)):
                    path = Path(source)
                    if not path.is_file():
                        continue
                    if not safe_rollout(home, source):
                        raise ValueError(f'会话 {sid} 的文件不在所选数据目录内。')
                    suffix = '.jsonl.zst' if path.name.endswith('.jsonl.zst') else '.jsonl'
                    name = f'files/{sid}/{n}{suffix}'
                    digest, size = hashlib.sha256(), 0
                    with path.open('rb') as src, archive.open(name, 'w', force_zip64=True) as dst:
                        for block in iter(lambda: src.read(1024 * 1024), b''):
                            size += len(block)
                            if size > MAX_MEMBER or total + size > MAX_TOTAL:
                                raise ValueError('导出包超过大小限制，请减少本次选择的会话。')
                            digest.update(block)
                            dst.write(block)
                    total += size
                    manifest['members'][name] = {'sha256': digest.hexdigest(), 'size': size}
                    record['files'].append({'member': name, 'primary': primary == canonical_path(path), 'suffix': suffix,
                                            'relative': canonical_path(path).relative_to(canonical_path(home)).as_posix()})
                payload = _json(record)
                if len(payload) > MAX_JSON or total + len(payload) > MAX_TOTAL:
                    raise ValueError('会话数据超过导出大小限制，请减少选择数量。')
                total += len(payload)
                name = f'sessions/{sid}.json'
                archive.writestr(name, payload)
                manifest['members'][name] = {'sha256': hashlib.sha256(payload).hexdigest(), 'size': len(payload)}
                manifest['sessions'].append({'id': sid, 'title': session.title, 'member': name})
            after = inventory(home)
            if after.warnings or before.signature(selected) != after.signature(selected):
                raise RuntimeError('导出期间会话发生变化，未生成导出包；请停止相关任务后重试。')
            archive.writestr('manifest.json', _json(manifest))
        os.replace(temporary, destination)
    finally:
        if Path(temporary).exists():
            Path(temporary).unlink()
    if progress:
        progress(len(selected), len(selected), '')
    return len(selected)


def _read_json(archive, name):
    if archive.getinfo(name).file_size > MAX_JSON:
        raise ValueError('导入包中的数据记录过大。')
    return json.loads(archive.read(name))


def _validate_record(record, entry, schemas):
    sid = entry['id']
    if str(uuid.UUID(sid)) != sid or record.get('id') != sid:
        raise ValueError('导入包包含无效或不一致的会话 ID。')
    if not isinstance(record.get('title'), str) or not isinstance(record.get('files'), list):
        raise ValueError('会话记录格式不正确。')
    tables = record.get('tables', {})
    if set(tables) - TABLES.keys():
        raise ValueError('导入包包含不支持的数据源。')
    for prefix, items in tables.items():
        if set(items) - set(TABLES[prefix]):
            raise ValueError('导入包包含不支持的数据表。')
        for table, rows in items.items():
            columns = {c[1] for c in schemas.get(prefix, {}).get(table, [])}
            for row in rows:
                if not isinstance(row, dict) or set(row) != columns:
                    raise ValueError('导入包的记录与存储结构不一致。')
                if row.get('id' if table == 'threads' else 'thread_id') != sid:
                    raise ValueError('导入包包含属于其他会话的记录。')
                if any(isinstance(v, (list, dict)) for v in row.values()):
                    raise ValueError('导入包包含无效字段类型。')
            if table == 'threads' and len(rows) > 1:
                raise ValueError('会话索引重复。')
    members = []
    for f in record['files']:
        if f.get('suffix') not in ('.jsonl', '.jsonl.zst'):
            raise ValueError('不支持的会话文件格式。')
        name = f.get('member', '')
        if not name.startswith(f'files/{sid}/') or '..' in name or '\\' in name:
            raise ValueError('导入包包含非法文件路径。')
        relative = f.get('relative', '')
        parts = relative.split('/')
        if (len(parts) < 2 or parts[0] not in ('sessions', 'archived_sessions')
                or any(not re.fullmatch(r'[A-Za-z0-9_.-]+', p) or p in ('.', '..') for p in parts)
                or not parts[-1].startswith('rollout-') or not parts[-1].endswith(f['suffix'])):
            raise ValueError('导入包包含非法会话目标路径。')
        members.append(name)
    if len(members) != len(set(members)) or sum(bool(f.get('primary')) for f in record['files']) > 1:
        raise ValueError('导入包包含重复会话文件。')


def inspect_bundle(home: Path, path: Path) -> ImportPlan:
    path = path.resolve()
    before_digest = _hash(path)
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = {i.filename for i in entries}
        if len(entries) != len(names) or len(entries) > 50000:
            raise ValueError('导入包包含重复文件或文件数量过多。')
        if any(i.file_size > MAX_MEMBER or i.flag_bits & 1 for i in entries) or sum(i.file_size for i in entries) > MAX_TOTAL:
            raise ValueError('导入包超过大小限制或使用了加密。')
        manifest = _read_json(archive, 'manifest.json')
        if manifest.get('format') != FORMAT or manifest.get('version') != VERSION:
            raise ValueError('请选择本软件导出的会话 ZIP 包；普通 ZIP 或其他版本格式暂不支持。')
        declared = manifest.get('members', {})
        if set(declared) | {'manifest.json'} != names:
            raise ValueError('导入包文件清单不一致。')
        for name, info in declared.items():
            if archive.getinfo(name).file_size != info.get('size'):
                raise ValueError('导入包文件大小校验失败。')
            with archive.open(name) as f:
                digest = hashlib.file_digest(f, 'sha256').hexdigest()
            if digest != info.get('sha256'):
                raise ValueError('导入包校验失败，文件可能已损坏。')
        schemas = manifest.get('schemas', {})
        if set(schemas) - TABLES.keys():
            raise ValueError('不支持的数据源。')
        for prefix, tables in schemas.items():
            if set(tables) - set(TABLES[prefix]):
                raise ValueError('不支持的数据表。')
        records, ids = [], set()
        used = set()
        for entry in manifest['sessions']:
            sid = entry['id']
            if sid in ids or entry['member'] != f'sessions/{sid}.json':
                raise ValueError('导入包包含重复会话或非法记录路径。')
            record = _read_json(archive, entry['member'])
            _validate_record(record, entry, schemas)
            ids.add(sid)
            records.append(record)
            used.add(entry['member'])
            used.update(f['member'] for f in record['files'])
        if not records or used != set(declared):
            raise ValueError('导入包为空或包含未关联文件。')
        for edge in manifest.get('edges', []):
            if not isinstance(edge, list) or len(edge) != 2 or any(sid not in ids for sid in edge):
                raise ValueError('导入包的父子关系不完整。')
    if _hash(path) != before_digest:
        raise ValueError('导入包在校验期间发生变化，请重新选择。')
    inv = inventory(home)
    if inv.warnings:
        raise ValueError('目标数据读取不完整：' + '；'.join(inv.warnings))
    return ImportPlan(path, before_digest, manifest, records, set(inv.sessions) & ids)


class ImportService:
    def __init__(self, home: Path, guard=external_codex_clients):
        self.home = home.resolve()
        self.guard = guard

    def _guard(self):
        if self.guard():
            raise RuntimeError('请先关闭 Codex 桌面应用和其他 Codex 客户端，再执行导入。')

    def _compatible(self, plan, candidates):
        paths = {}
        for prefix in TABLES:
            required = {t for r in candidates for t, rows in r['tables'].get(prefix, {}).items() if rows}
            if not required:
                continue
            path = db_file(self.home, prefix)
            if not path:
                raise ValueError('目标缺少会话数据库，请先用相同版本的 Codex 创建一次会话并关闭后重试。')
            with connect_readonly(path) as con:
                for table in required:
                    if _schema(con, table) != plan.manifest['schemas'][prefix][table]:
                        raise ValueError('导出来源与当前 Codex 的存储结构不同，未导入。请使用相同版本的 Codex。')
            paths[prefix] = path
        return paths

    def execute(self, plan: ImportPlan, progress=None) -> TransferResult:
        self._guard()
        # Revalidate the exact archive immediately before changing the destination.
        current = inspect_bundle(self.home, plan.path)
        if current.digest != plan.digest:
            raise ValueError('导入包已变化，请重新选择并确认。')
        candidates = [r for r in current.records if r['id'] not in current.existing]
        paths = self._compatible(current, candidates)
        result = TransferResult(skipped=sorted(current.existing))
        if not candidates:
            return result
        self._guard()
        # ZIP member names are never extracted. Only validated native session
        # paths are restored, preserving filenames needed by history lineage.
        with zipfile.ZipFile(current.path) as archive, tempfile.TemporaryDirectory(prefix='.codex-import-', dir=self.home) as stage:
            with ExitStack() as stack:
                con = sqlite3.connect(':memory:', timeout=3, isolation_level=None)
                stack.callback(con.close)
                for prefix, path in paths.items():
                    con.execute(f'ATTACH DATABASE ? AS "{prefix}"', (str(path),))
                for i, record in enumerate(candidates):
                    sid, created = record['id'], []
                    if progress:
                        progress(i, len(candidates), record['title'])
                    try:
                        self._guard()
                        latest = inventory(self.home)
                        if latest.warnings:
                            raise RuntimeError('目标会话读取不完整，已停止导入。')
                        if sid in latest.sessions:
                            result.skipped.append(sid)
                            continue
                        folder = self.home / ('archived_sessions' if record['archived'] else 'sessions') / datetime.now().strftime('%Y/%m/%d')
                        primary = None
                        for n, f in enumerate(record['files']):
                            target = self.home.joinpath(*f['relative'].split('/'))
                            if not safe_rollout(self.home, str(target)) or target.exists():
                                raise ValueError('目标文件已存在或超出会话目录，未覆盖。')
                            target.parent.mkdir(parents=True, exist_ok=True)
                            staging = Path(stage) / (str(n) + f['suffix'])
                            with archive.open(f['member']) as src, staging.open('wb') as dst:
                                shutil.copyfileobj(src, dst, 1024 * 1024)
                            # Recheck bytes from the opened archive before commit.
                            if _hash(staging) != current.manifest['members'][f['member']]['sha256']:
                                raise ValueError('导入包内容发生变化。')
                            meta = rollout_meta(staging)
                            if (meta.get('id') or meta.get('session_id')) != sid:
                                raise ValueError('会话文件内容与清单中的 ID 不一致。')
                            with staging.open('rb') as src, target.open('xb') as dst:
                                created.append(target)
                                shutil.copyfileobj(src, dst, 1024 * 1024)
                            if f.get('primary') or primary is None:
                                primary = target
                        con.execute('BEGIN IMMEDIATE')
                        # Attached databases participate in one SQL transaction.
                        for prefix in ('thread_history', 'state'):
                            if prefix not in paths:
                                continue
                            for table in TABLES[prefix]:
                                for raw in record['tables'].get(prefix, {}).get(table, []):
                                    row = dict(raw)
                                    if table == 'threads':
                                        row['rollout_path'] = str(primary or folder / f'rollout-{sid}.jsonl')
                                        # Machine-specific sidebar grouping is not portable.
                                        for key in ('project_id', 'thread_section_id', 'section_position', 'section_entered_at_ms'):
                                            if key in row:
                                                row[key] = None
                                        if 'is_pinned' in row:
                                            row['is_pinned'] = 0
                                    columns = list(row)
                                    names = ','.join('"' + c + '"' for c in columns)
                                    values = ','.join('?' for _ in columns)
                                    con.execute(f'INSERT INTO "{prefix}"."{table}" ({names}) VALUES ({values})', [row[c] for c in columns])
                        self._guard()
                        con.commit()
                        result.succeeded.append(sid)
                    except Exception as exc:
                        con.rollback()
                        # Only this import's newly created session files are removed.
                        for target in created:
                            if safe_rollout(self.home, str(target)):
                                target.unlink(missing_ok=True)
                        result.failures[sid] = str(exc)
                        # A failed import stops the batch; already imported sessions remain.
                        for remaining in candidates[i + 1:]:
                            result.failures[remaining['id']] = '前一项失败，尚未导入。'
                        break
                if 'state' in paths and result.succeeded:
                    available = set(inventory(self.home).sessions)
                    try:
                        self._guard()
                        con.execute('BEGIN IMMEDIATE')
                        for parent, child in current.manifest.get('edges', []):
                            if parent in available and child in result.succeeded:
                                con.execute('INSERT OR IGNORE INTO state.thread_spawn_edges(parent_thread_id,child_thread_id,status) VALUES (?,?,?)',
                                            (parent, child, 'completed'))
                        con.commit()
                    except Exception as exc:
                        con.rollback()
                        result.failures['父子关系'] = '会话已导入，但父子关系写入失败：' + str(exc)
        if progress:
            progress(len(candidates), len(candidates), '')
        return result
