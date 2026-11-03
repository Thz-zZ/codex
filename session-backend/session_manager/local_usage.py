from __future__ import annotations

from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import zstandard

from .store import open_rollout, walk_rollouts
from .model_pricing import LocalModelPricing

SHANGHAI = timezone(timedelta(hours=8))
MAX_SAFE_INTEGER = 9_007_199_254_740_991


def _number(value):
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)) and 0 <= value <= MAX_SAFE_INTEGER:
        return int(value)
    return None


def _date_range(period, now=None):
    today = (now or datetime.now(SHANGHAI)).astimezone(SHANGHAI).date()
    if period == "today":
        start = end = today
    elif period == "yesterday":
        start = end = today - timedelta(days=1)
    elif period in ("7", "30", "all"):
        end = today
        start = today - timedelta(days={"7": 6, "30": 29, "all": 89}[period])
    elif period.startswith("range:"):
        try:
            _, first, last = period.split(":")
            start, end = date.fromisoformat(first), date.fromisoformat(last)
        except (ValueError, TypeError):
            raise ValueError("请选择有效日期，单次查询最多 90 天。") from None
        if start > end or end > today or (end - start).days >= 90:
            raise ValueError("请选择有效日期，单次查询最多 90 天。")
    else:
        raise ValueError("未知的时间范围。")
    return {"period": period, "start": start.isoformat(), "end": end.isoformat(),
            "days": (end - start).days + 1, "timezone": "Asia/Shanghai"}


def _event_time(value):
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if value > 10**11:
            value /= 1000
        try:
            return datetime.fromtimestamp(value, SHANGHAI)
        except (OverflowError, OSError, ValueError):
            return None
    if isinstance(value, str):
        try:
            parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
            return parsed.replace(tzinfo=SHANGHAI) if parsed.tzinfo is None else parsed.astimezone(SHANGHAI)
        except ValueError:
            return None
    return None


def _usage_values(value, previous_total):
    info = value if isinstance(value, dict) else {}
    last = info.get("last_token_usage")
    total = info.get("total_token_usage")
    if isinstance(total, dict) and previous_total and all(
            _number(total.get(key)) == previous_total.get(key)
            for key in ("input_tokens", "cached_input_tokens", "output_tokens")):
        return None, previous_total
    if isinstance(last, dict):
        usage = last
    elif isinstance(total, dict):
        usage = {}
        for key in ("input_tokens", "cached_input_tokens", "output_tokens", "cache_write_input_tokens"):
            current = _number(total.get(key))
            previous = previous_total.get(key)
            if current is None:
                if key == "cache_write_input_tokens":
                    continue
                return None, previous_total
            usage[key] = current - previous if previous is not None and current >= previous else current
    else:
        usage = info

    next_total = dict(previous_total)
    if isinstance(total, dict):
        for key in ("input_tokens", "cached_input_tokens", "output_tokens", "cache_write_input_tokens"):
            number = _number(total.get(key))
            if number is not None:
                next_total[key] = number

    input_tokens = _number(usage.get("input_tokens"))
    cached = _number(usage.get("cached_input_tokens"))
    output = _number(usage.get("output_tokens"))
    if input_tokens is None or cached is None or output is None or cached > input_tokens or not (input_tokens or output):
        return None, next_total
    write = _number(usage.get("cache_write_input_tokens"))
    if write is not None and write + cached > input_tokens:
        write = None
    return {"input": input_tokens, "read": cached, "write": write, "output": output}, next_total


def _http_status(*sources):
    for source in sources:
        if not isinstance(source, dict):
            continue
        candidates = [source.get(key) for key in (
            "http_status_code", "status_code", "http_status",
            "httpStatusCode", "statusCode", "httpStatus")]
        for key in ("response", "http_response"):
            response = source.get(key)
            if isinstance(response, dict):
                candidates.extend(response.get(field) for field in (
                    "status_code", "statusCode", "status"))
        for value in candidates:
            if isinstance(value, str) and len(value.strip()) == 3 and value.strip().isascii() and value.strip().isdigit():
                value = int(value.strip())
            if type(value) is int and 100 <= value <= 599:
                return value
    return None


def _parse_event(line, context, previous_total):
    try:
        event = json.loads(line)
    except (ValueError, TypeError):
        return None, context, previous_total
    if not isinstance(event, dict):
        return None, context, previous_total

    payload = event.get("payload") if isinstance(event.get("payload"), dict) else {}
    event_type = event.get("type", "")
    payload_type = payload.get("type", "")
    if event_type == "session_meta":
        context = {**context, "session": payload.get("id", context.get("session", ""))}
    if event_type == "turn_context" or payload_type == "turn_context":
        source = payload
        context = {**context, "model": source.get("model") or source.get("model_name") or context.get("model", "")}

    if event_type == "event_msg" and payload_type == "token_count":
        info = payload.get("info") if isinstance(payload.get("info"), dict) else payload
    elif event_type == "token_count":
        info = payload or event.get("info", {})
    else:
        return None, context, previous_total

    values, previous_total = _usage_values(info, previous_total)
    if values is None:
        return None, context, previous_total
    timestamp = _event_time(event.get("timestamp") or payload.get("timestamp"))
    if timestamp is None:
        return None, context, previous_total
    return {**values, "timestamp": timestamp.timestamp(), "date": timestamp.date().isoformat(),
            "hour": timestamp.strftime("%Y-%m-%d %H:00"),
            "model": str(info.get("model") or context.get("model") or "未记录模型"),
            "statusCode": _http_status(info.get("last_token_usage"), info, payload, event)}, context, previous_total


def _fingerprint(path, offset):
    with path.open("rb") as stream:
        head = stream.read(min(4096, offset))
        stream.seek(max(0, offset - 4096))
        tail = stream.read(min(4096, offset))
    return hashlib.sha256(head).digest(), hashlib.sha256(tail).digest()


class LocalUsageReader:
    """Cache hourly totals and recent records, then read only appended JSONL lines."""

    def __init__(self, *, pricing=None):
        self.files = {}
        self.session_counts = None
        self.pricing = pricing if pricing is not None else LocalModelPricing()

    @staticmethod
    def _record(line, context, previous_total, buckets, logs):
        # Transcript bodies can be large; no need to decode unrelated messages.
        if not any(kind in line for kind in ('"token_count"', '"turn_context"', '"session_meta"')):
            return context, previous_total
        row, context, previous_total = _parse_event(line, context, previous_total)
        if row:
            key = (row["hour"], row["model"])
            bucket = buckets.setdefault(key, {"date": row["date"], "hour": row["hour"],
                "model": row["model"], "input": 0, "read": 0, "write": 0, "costWrite": 0, "output": 0, "records": 0, "hits": 0})
            for field in ("input", "read", "output"):
                bucket[field] += row[field]
            bucket["records"] += 1
            bucket["hits"] += row["read"] > 0
            bucket["costWrite"] += row["write"] or 0
            bucket["write"] = bucket["write"] + row["write"] if bucket["write"] is not None and row["write"] is not None else None
            recent = logs.setdefault(row["model"], [])
            recent.append(row)
            if len(recent) > 300:
                del recent[:-300]
        return context, previous_total

    def _parse_plain(self, path, previous=None):
        stat = path.stat()
        identity = (stat.st_dev, stat.st_ino)
        offset = 0
        context, previous_total, buckets, logs = {}, {}, {}, {}
        if previous and previous["identity"] == identity and stat.st_size >= previous["offset"]:
            head, tail = _fingerprint(path, previous["offset"])
            if head == previous["head"] and tail == previous["tail"]:
                offset = previous["offset"]
                context = dict(previous["context"])
                previous_total = dict(previous["previous_total"])
                buckets = {key: dict(row) for key, row in previous["buckets"].items()}
                logs = {key: list(rows) for key, rows in previous["logs"].items()}

        with path.open("rb") as stream:
            stream.seek(offset)
            complete_offset = offset
            for raw in stream:
                if not raw.endswith(b"\n"):
                    break
                complete_offset += len(raw)
                context, previous_total = self._record(raw.decode("utf-8", errors="replace"),
                    context, previous_total, buckets, logs)

        head, tail = _fingerprint(path, complete_offset)
        return {"identity": identity, "signature": (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns),
                "offset": complete_offset, "head": head, "tail": tail,
                "context": context, "previous_total": previous_total, "buckets": buckets, "logs": logs}

    def _parse_compressed(self, path, stat):
        context, previous_total, buckets, logs = {}, {}, {}, {}
        with open_rollout(path) as stream:
            for line in stream:
                context, previous_total = self._record(line, context, previous_total, buckets, logs)
        return {"identity": (stat.st_dev, stat.st_ino),
                "signature": (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns),
                "context": context, "buckets": buckets, "logs": logs}

    def read(self, home: Path, period="today", model="all", *, now=None):
        range_info = _date_range(period, now)
        selected, logs, warnings = [], [], []
        error_count = 0
        sessions = {}
        seen = set()
        for path, _archived in walk_rollouts(home):
            key = str(path)
            seen.add(key)
            try:
                stat = path.stat()
                signature = (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)
                previous = self.files.get(key)
                if previous and previous["signature"] == signature:
                    parsed = previous
                elif path.suffix == ".zst":
                    parsed = self._parse_compressed(path, stat)
                else:
                    parsed = self._parse_plain(path, previous)
                self.files[key] = parsed
                # An archived/exported duplicate of the same session is counted once.
                session = parsed["context"].get("session") or key
                prior = sessions.get(session)
                count = sum(row["records"] for row in parsed["buckets"].values())
                if prior is None or count > prior[0]:
                    sessions[session] = (count, parsed)
            except (OSError, ValueError, EOFError, zstandard.ZstdError):
                error_count += 1
                warnings.append("有会话文件无法读取，本地统计可能不完整。")
                continue
        for key in self.files.keys() - seen:
            del self.files[key]
        model_names = set()
        for _, parsed in sessions.values():
            for row in parsed["buckets"].values():
                if range_info["start"] <= row["date"] <= range_info["end"]:
                    model_names.add(row["model"])
                    if model == "all" or row["model"] == model:
                        selected.append(row)
            for name, recent in parsed["logs"].items():
                if model == "all" or name == model:
                    logs.extend(row for row in recent if range_info["start"] <= row["date"] <= range_info["end"])
        counts = {session: count for session, (count, _) in sessions.items()}
        previous_counts = self.session_counts or {}
        pricing = self.pricing.load(home)
        priced_rows = [row | {"cost": pricing.estimate(row)} for row in selected]
        priced_logs = [row | {"cost": pricing.estimate(row)} for row in logs]
        result = _aggregate(priced_rows, priced_logs, range_info, model_names, warnings + pricing.warnings)
        result["pricing"] = {"source": pricing.source, "currency": "USD", "estimated": True,
                             "missingModels": sorted({row["model"] for row in priced_rows
                                                      if row["cost"] is None}, key=str.casefold)}
        result["sync"] = {"added": sum(max(0, count - previous_counts.get(session, 0))
                                     for session, count in counts.items()),
                          "scannedFiles": len(seen), "errors": error_count,
                          "initial": self.session_counts is None}
        # Failed files may reappear on the next scan; keep their previous baseline.
        self.session_counts = (previous_counts | counts) if error_count else counts
        return result


def _aggregate(rows, logs, range_info, model_names, warnings):
    def group(field):
        grouped = defaultdict(list)
        for row in rows:
            grouped[row[field]].append(row)
        return [_totals(items) | {"name": name} for name, items in sorted(grouped.items())]

    ordered = sorted(logs, key=lambda row: row["timestamp"], reverse=True)
    return {"mode": "local", "totals": _totals(rows),
            "models": [_totals(items) | {"name": name} for name, items in sorted(
                _group_rows(rows, "model").items(), key=lambda item: item[0].casefold())],
            "daily": group("date"), "hourly": group("hour"),
            "logs": ordered[:300],
            "modelNames": sorted(model_names, key=str.casefold),
            "range": range_info,
            "warnings": sorted(set(warnings)),
            "note": "Codex 本地会话日志统计，覆盖全部本地会话，不区分中转。总成本按本地模型单价估算，不代表中转实际扣费；缺少单价时不显示完整总成本。HTTP 状态码仅使用日志明确记录的字段，缺失时显示未知，实际 HTTP 请求数不作推算。明细最多展示 300 条近期记录，汇总包含全部有效记录。"}


def _group_rows(rows, field):
    grouped = defaultdict(list)
    for row in rows:
        grouped[row[field]].append(row)
    return grouped


def _totals(rows):
    input_tokens = sum(row["input"] for row in rows)
    read_tokens = sum(row["read"] for row in rows)
    output_tokens = sum(row["output"] for row in rows)
    write_tokens = sum(row["write"] for row in rows) if rows and all(row.get("write") is not None for row in rows) else None
    count = sum(row.get("records", 1) for row in rows)
    hits = sum(row.get("hits", row["read"] > 0) for row in rows)
    return {"input": input_tokens, "read": read_tokens, "write": write_tokens,
            "plain": input_tokens - read_tokens, "output": output_tokens,
            "totalTokens": input_tokens + output_tokens,
            "requests": count, "records": count,
            "cost": sum(row["cost"] for row in rows) if all(row.get("cost") is not None for row in rows) else None,
            "tokenRate": read_tokens / input_tokens if input_tokens else None,
            "requestRate": hits / count if count else None,
            "excluded": 0}
