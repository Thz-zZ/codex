"""A local, stdio-only Codex App Server client. Never starts a model turn."""
from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time


class RpcError(RuntimeError):
    pass


def discover_codex() -> str:
    found = shutil.which("codex.exe") or shutil.which("codex")
    if found and Path(found).suffix.lower() == ".exe":
        return found
    local = Path(os.environ.get("LOCALAPPDATA", ""))
    candidates = list((local / "OpenAI/Codex/bin").glob("*/codex.exe"))
    candidates += list((Path.home() / ".vscode/extensions").glob("openai.chatgpt-*/bin/windows-x86_64/codex.exe"))
    candidates = [p for p in candidates if p.is_file()]
    return str(max(candidates, key=lambda p: p.stat().st_mtime)) if candidates else ""


class CodexRpc:
    def __init__(self, executable: str, home: Path):
        self.executable, self.home = executable, home.resolve()
        self.process = None
        self._lock = threading.RLock()
        self._queue = queue.Queue()
        self._sequence = 0
        self.notifications = []

    @property
    def pid(self):
        return self.process.pid if self.process and self.process.poll() is None else None

    def _start(self):
        if self.process and self.process.poll() is None:
            return
        if not self.executable or not Path(self.executable).is_file():
            raise RpcError("未找到 codex.exe，请在“设置”中选择已安装的 Codex 程序。")
        env = os.environ.copy()
        env["CODEX_HOME"] = str(self.home)
        # A fresh process for this explicitly selected home; no TCP listener.
        args = [self.executable, "app-server", "--listen", "stdio://",
                "-c", "analytics.enabled=false",
                "-c", "features.background_paginated_rollout_migration=false"]
        self._queue = queue.Queue()
        try:
            self.process = subprocess.Popen(
                args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace",
                env=env, cwd=str(self.home), bufsize=1,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError as exc:
            raise RpcError(f"无法启动 Codex：{exc}") from exc
        process, inbox = self.process, self._queue

        def read():
            try:
                for line in process.stdout:
                    try:
                        inbox.put(json.loads(line))
                    except ValueError:
                        continue
            finally:
                inbox.put({"_eof": True})

        threading.Thread(target=read, daemon=True).start()
        self._exchange("initialize", {
            "clientInfo": {"name": "codex_local_session_manager", "title": "Local Session Manager", "version": "1.0.0"},
            "capabilities": {"experimentalApi": True}}, 15)
        self._send({"method": "initialized", "params": {}})

    def _send(self, value):
        try:
            self.process.stdin.write(json.dumps(value, ensure_ascii=False) + "\n")
            self.process.stdin.flush()
        except (OSError, ValueError) as exc:
            raise RpcError("Codex 本地连接已断开，请刷新后重试。") from exc

    def _exchange(self, method, params, timeout):
        self._sequence += 1
        request_id = self._sequence
        self._send({"id": request_id, "method": method, "params": params})
        end = time.monotonic() + timeout
        while True:
            try:
                response = self._queue.get(timeout=max(0.01, end - time.monotonic()))
            except queue.Empty as exc:
                raise RpcError(f"本地接口 {method} 超时；操作结果需刷新核实。") from exc
            if response.get("_eof"):
                raise RpcError("Codex 本地服务已退出，请检查 Codex 版本及配置。")
            if response.get("id") == request_id:
                if "error" in response:
                    error = response["error"]
                    raise RpcError(f"{method}：{error.get('message', str(error))}")
                return response.get("result", {})
            if "id" in response and "method" in response:
                # This application never grants tools, authentication, or approval requests.
                self._send({"id": response["id"], "error": {"code": -32601, "message": "Unsupported by read/delete client"}})
            elif response.get("method") == "thread/deleted":
                self.notifications.append(response.get("params", {}).get("threadId"))
            if time.monotonic() >= end:
                raise RpcError(f"本地接口 {method} 超时；操作结果需刷新核实。")

    def call(self, method, params=None, timeout=15):
        with self._lock:
            self._start()
            return self._exchange(method, params or {}, timeout)

    def list_threads(self):
        kinds = ["cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview",
                 "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown"]
        threads = []
        for archived in (False, True):
            cursor, seen = None, set()
            while True:
                result = self.call("thread/list", {
                    "limit": 100, "cursor": cursor, "archived": archived,
                    "modelProviders": [], "sourceKinds": kinds, "useStateDbOnly": True,
                    "sortKey": "updated_at"})
                for row in result.get("data", []):
                    row = dict(row)
                    row["archived"] = archived
                    threads.append(row)
                cursor = result.get("nextCursor")
                if not cursor:
                    break
                if cursor in seen:
                    raise RpcError("会话列表返回重复分页游标，已停止加载。")
                seen.add(cursor)
        return threads

    def close(self):
        with self._lock:
            process, self.process = self.process, None
            if not process:
                return
            try:
                process.stdin.close()
                process.wait(timeout=2)
            except (OSError, ValueError, subprocess.TimeoutExpired):
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=2)
