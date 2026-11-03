"""Local USD estimates; prices are never fetched during usage synchronization."""
from __future__ import annotations

from decimal import Decimal, InvalidOperation
import json
from pathlib import Path


PRICE_FIELDS = ("inputCostPerMillion", "outputCostPerMillion",
                "cacheReadCostPerMillion", "cacheCreationCostPerMillion")
# Snapshot of this workspace's CC Switch model-pricing.json, 2026-11-02.
# These are reference prices, not a relay's billing rates. Exact IDs only.
REFERENCE_PRICES = {
    "gpt-6-astra": ("10", "50", "1", "12.5"),
    "gpt-6-luna": ("0.1", "0.5", "0.01", "0.125"),
    "gpt-6-sol": ("2", "10", "0.2", "2.5"),
    "gpt-6.1-sol": ("2", "10", "0.1", "2.5"),
    "gpt-6.1-terra": ("1", "5", "0.05", "1.25"),
    "gpt-daybreak-blue-latest": ("4", "20", "0.4", "5"),
    "gpt-daybreak-red-latest": ("12.5", "75", "1.25", "15.625"),
}


def _rates(values):
    try:
        rates = tuple(Decimal(str(value)) for value in values)
        if len(rates) == 4 and all(value.is_finite() and 0 <= value <= 1_000_000 for value in rates):
            return rates
    except (InvalidOperation, ValueError):
        pass
    return None


class LocalModelPricing:
    def __init__(self, cc_switch_path=None):
        self.cc_switch_path = (Path(cc_switch_path) if cc_switch_path is not None
                               else Path.home() / ".cc-switch" / "model-pricing.json")
        self.signature = None
        self.prices = {}
        self.source = "内置参考价格 · 2026-11-02"
        self.warnings = []

    def load(self, home):
        # A Codex home's explicit override takes precedence over CC Switch.
        paths = (Path(home) / "relay-model-pricing.json", self.cc_switch_path)
        signature = []
        for path in paths:
            try:
                stat = path.stat()
                signature.append((str(path), stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns))
            except OSError:
                signature.append((str(path), None))
        if signature == self.signature:
            return self
        self.signature = signature
        self.prices = {name: _rates(values) for name, values in REFERENCE_PRICES.items()}
        self.source = "内置参考价格 · 2026-11-02"
        self.warnings = []
        for path, state in zip(paths, signature):
            if state[1] is None:
                continue
            try:
                if state[1] > 2 * 1024 * 1024:
                    raise ValueError("pricing file too large")
                data = json.loads(path.read_text(encoding="utf-8-sig"))
                models = data["models"]
                if not isinstance(models, list):
                    raise ValueError("invalid models")
                prices = {}
                for row in models:
                    if not isinstance(row, dict):
                        continue
                    name = row.get("modelId")
                    if isinstance(name, str) and name.strip():
                        # Keep invalid entries unknown instead of silently falling
                        # back to a different price for an explicitly listed model.
                        prices[name.strip().casefold()] = _rates(row.get(key) for key in PRICE_FIELDS)
                self.prices.update(prices)
                self.source = "本地自定义价格" if path == paths[0] else "CC Switch 本地价格"
                break
            except (OSError, ValueError, KeyError, TypeError):
                self.warnings.append("本地模型价格表无法读取，已使用备用参考价格估算。")
        return self

    def estimate(self, row):
        rates = self.prices.get(row["model"].strip().casefold())
        if rates is None:
            return None
        input_price, output_price, read_price, write_price = rates
        write = row.get("costWrite", row.get("write")) or 0
        # Codex's input includes both cache reads and writes. Output already
        # includes reasoning tokens; neither category is billed a second time.
        value = ((row["input"] - row["read"] - write) * input_price
                 + row["read"] * read_price + write * write_price
                 + row["output"] * output_price) / 1_000_000
        return float(value)
