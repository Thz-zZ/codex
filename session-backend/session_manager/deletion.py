"""Official transcript deletion followed by scoped desktop catalog cleanup."""
from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path

import psutil

from .store import Inventory, InventoryReader, inventory, safe_rollout
from .catalog import check_catalog, clean_deleted_catalog


class DeletionBlocked(RuntimeError):
    pass


class ScopeChanged(DeletionBlocked):
    pass


@dataclass
class DeletePlan:
    selected: frozenset[str]
    affected: frozenset[str]
    signature: tuple
    snapshot: Inventory


@dataclass
class DeleteResult:
    succeeded: list[str]
    failures: dict[str, str]
    stopped: str = ""


def external_codex_clients(own_pid=None):
    """Fail closed if another local writer might own a thread.

    A private app-server cannot observe another process's active threads. Global
    process exclusion is deliberately conservative, including other CODEX_HOMEs.
    """
    ignored = {os.getpid()}
    if own_pid:
        ignored.add(own_pid)
        # This client never starts model turns or Codex child workers. Exempt
        # only its exact server PID; any additional Codex process blocks writes.
    found = []
    for process in psutil.process_iter(["pid", "name"]):
        try:
            if process.pid in ignored:
                continue
            name = (process.info.get("name") or "").lower()
            if name in ("codex.exe", "codex", "codex-cli.exe", "codex-app.exe"):
                found.append(f"{name}（PID {process.pid}）")
        except psutil.NoSuchProcess:
            continue
        except psutil.AccessDenied as exc:
            raise DeletionBlocked("无法确认其他 Codex 进程状态，请关闭客户端后重试。") from exc
    return found


class DeletionService:
    def __init__(self, home: Path, rpc, guard=external_codex_clients, reader=None):
        self.home, self.rpc, self.guard = home.resolve(), rpc, guard
        self.reader = reader if reader is not None else InventoryReader()

    def _read_scope(self, ids):
        if isinstance(self.reader, InventoryReader):
            return self.reader(self.home, focus=ids)
        return self.reader(self.home)

    def _guard(self):
        others = self.guard(self.rpc.pid)
        if others:
            raise DeletionBlocked("请先关闭 Codex 桌面应用，以及 VS Code 中的 Codex 扩展/终端，然后在本软件重试。\n"
                                  "独立服务无法确认这些客户端是否正在写入会话：\n" + "\n".join(others))

    def _check(self, inv, affected):
        if inv.warnings:
            raise DeletionBlocked("数据读取不完整，暂不能确认删除范围：\n" + "\n".join(inv.warnings[:5]))
        for sid in affected:
            s = inv.sessions.get(sid)
            if s is None:
                # Edges can survive a removed descendant; a record with no content
                # needn't cause deletion of an unrelated path.
                continue
            if s.api_status == "active":
                raise DeletionBlocked(f"会话“{s.title[:80]}”正在运行，请结束任务后重试。")
            if any(not safe_rollout(self.home, p) for p in s.paths):
                raise DeletionBlocked(f"会话 {sid} 的记录指向所选数据目录之外，已停止删除。")

    def prepare(self, selected) -> DeletePlan:
        selected = frozenset(selected)
        if not selected:
            raise DeletionBlocked("请先勾选需要删除的会话。")
        self._guard()
        check_catalog(self.home)
        # API errors must not be silently swallowed in a destructive operation.
        rows = self.rpc.list_threads()
        inv = self.reader(self.home, rows)
        if selected - inv.sessions.keys():
            raise ScopeChanged("所选会话已发生变化，请刷新列表后重新选择。")
        affected = frozenset(inv.affected(selected))
        self._check(inv, affected)
        self._guard()
        return DeletePlan(selected, affected, inv.signature(affected), inv)

    def execute(self, plan: DeletePlan, progress=None) -> DeleteResult:
        # Merely preparing or dismissing a plan can never delete anything.
        self._guard()
        check_catalog(self.home)
        current = self.reader(self.home, self.rpc.list_threads())
        affected = current.affected(plan.selected)
        self._check(current, affected)
        if affected != set(plan.affected) or current.signature(affected) != plan.signature:
            raise ScopeChanged("会话内容或父子关系已变化，未执行删除。请重新点击删除并核对新的确认列表。")
        remaining = set(plan.affected)
        roots = set(plan.selected)
        # Selected descendants of another selected root need no second request.
        for sid in list(roots):
            if any(sid in current.affected({other}) for other in roots if other != sid):
                roots.discard(sid)
        if not roots:
            raise DeletionBlocked("检测到异常父子循环，未执行删除。")
        result = DeleteResult([], {})
        for index, root in enumerate(sorted(roots)):
            try:
                self._guard()
                fresh = self._read_scope(plan.snapshot.affected({root}))
                scope = fresh.affected({root})
                self._check(fresh, scope)
                if not scope <= plan.affected or fresh.signature(scope) != plan.snapshot.signature(scope):
                    result.stopped = "检测到新的会话内容或子会话，已停止剩余删除，请重新确认。"
                    break
                self._guard()
                if progress:
                    progress(index, len(roots), root)
                call_error = ""
                try:
                    self.rpc.call("thread/delete", {"threadId": root}, timeout=30)
                except Exception as exc:
                    call_error = str(exc)
                # Verify persistent state even after timeout/partial server failure.
                after = self._read_scope(scope)
                if after.warnings:
                    raise DeletionBlocked("删除结果无法完整验证，请刷新后检查：" + "；".join(after.warnings[:3]))
                for sid in sorted(scope):
                    if sid not in after.sessions:
                        result.succeeded.append(sid)
                        remaining.discard(sid)
                    else:
                        result.failures[sid] = call_error or "接口已返回，但本地记录仍存在；未确认删除成功。"
                if call_error and ("超时" in call_error or "断开" in call_error or "退出" in call_error):
                    result.stopped = "本地服务连接异常，已停止剩余操作；请刷新核对结果。"
                    break
            except (DeletionBlocked, OSError, RuntimeError) as exc:
                result.stopped = str(exc)
                break
        if result.succeeded:
            try:
                clean_deleted_catalog(self.home, result.succeeded,
                                      lambda home: self._read_scope(result.succeeded), self._guard)
            except (RuntimeError, OSError) as exc:
                for sid in result.succeeded:
                    result.failures[sid] = "会话内容已删除，但桌面目录清理未完成：" + str(exc)
                result.succeeded.clear()
        if progress:
            progress(len(roots), len(roots), "")
        if result.stopped:
            for sid in remaining:
                result.failures.setdefault(sid, "未执行或未能验证：" + result.stopped)
        return result
