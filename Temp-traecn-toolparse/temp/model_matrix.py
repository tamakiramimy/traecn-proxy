#!/usr/bin/env python3
"""跨全部 __dev 模型验证 thinking / tool_use / 泄漏三条链路。"""
import json
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

BASE = "http://127.0.0.1:10005/v1/messages"
KEY = open("/tmp/.traecn-key").read().strip()

TOOLS = [{
    "name": "Read",
    "description": "Read a file from disk",
    "input_schema": {
        "type": "object",
        "properties": {"file_path": {"type": "string"}},
        "required": ["file_path"],
    },
}]


def run(model: str) -> dict:
    body = {
        "model": model,
        "stream": True,
        "max_tokens": 2048,
        "thinking": {"type": "enabled", "budget_tokens": 1024},
        "messages": [{"role": "user", "content": "读取 /etc/hostname。直接调用 Read 工具，不要解释。"}],
        "tools": TOOLS,
    }
    started = time.time()
    proc = subprocess.run(
        ["curl", "-s", "--max-time", "300", BASE,
         "-H", f"x-api-key: {KEY}", "-H", "content-type: application/json",
         "-d", json.dumps(body, ensure_ascii=False)],
        capture_output=True, text=True,
    )
    raw = proc.stdout
    elapsed = time.time() - started

    thinking = tool_use = 0
    text = []
    error = None
    tool_names = []
    for line in raw.splitlines():
        if not line.startswith("data: "):
            continue
        try:
            payload = json.loads(line[6:])
        except json.JSONDecodeError:
            continue
        kind = payload.get("type")
        if kind == "content_block_start":
            block = payload.get("content_block", {})
            if block.get("type") == "thinking":
                thinking += 1
            elif block.get("type") == "tool_use":
                tool_use += 1
                tool_names.append(block.get("name"))
        elif kind == "content_block_delta":
            delta = payload.get("delta", {})
            if delta.get("type") == "text_delta":
                text.append(delta.get("text", ""))
        elif kind == "error":
            error = payload.get("error", {}).get("message")

    visible = "".join(text)
    return {
        "model": model,
        "sec": round(elapsed, 1),
        "thinking": thinking,
        "tool_use": tool_use,
        "tools": ",".join(n for n in tool_names if n),
        "leak": any(marker in visible for marker in ("<tool_call", "<parameter", "\uff5cDSML\uff5c")),
        "text_len": len(visible),
        "error": error,
    }


models = sys.argv[1:]
results = []
with ThreadPoolExecutor(max_workers=3) as pool:
    for result in pool.map(run, models):
        results.append(result)
        flag = "ERR " if result["error"] else ("LEAK" if result["leak"] else "ok  ")
        print(f"{flag} {result['model']:<36} {result['sec']:>6}s think={result['thinking']} "
              f"tool={result['tool_use']}({result['tools']}) text={result['text_len']} "
              f"{result['error'] or ''}", flush=True)

json.dump(results, open("/tmp/traecn-matrix.json", "w"), ensure_ascii=False, indent=2)
ok = [r for r in results if not r["error"] and r["tool_use"] and not r["leak"]]
print(f"\n汇总: {len(ok)}/{len(results)} 模型 tool_use 正常且无泄漏")
print(f"有 thinking 的: {sum(1 for r in results if r['thinking'])}/{len(results)}")
