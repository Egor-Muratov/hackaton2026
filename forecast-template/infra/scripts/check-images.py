#!/usr/bin/env python3
"""Сверяет ссылки на базовые образы с infra/images.lock (единственный источник правды).

  check-images.py          — проверка: код 1, если где-то версия расходится с lock или стоит плавающий FROM
  check-images.py --fix    — переписать все ссылки значениями из lock (после pin-images.sh)

Где ищет: Dockerfile (`ARG VAR=...`), docker-compose (`${VAR:-...}`), .gitlab-ci.yml (`  VAR: ...`).
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / "infra" / "images.lock"
FILES = ["api/Dockerfile", "ml/Dockerfile", "frontend/Dockerfile",
         "infra/docker-compose.yml", ".gitlab-ci.yml"]


def load_lock() -> dict[str, str]:
    out = {}
    for line in LOCK.read_text(encoding="utf-8").splitlines():
        if line.strip() and not line.lstrip().startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def patterns(var: str) -> list[re.Pattern]:
    return [
        re.compile(rf"^(ARG {var}=)(\S+)", re.M),             # Dockerfile
        re.compile(rf"(\$\{{{var}:-)([^}}]+)(\}})"),           # compose
        re.compile(rf"^(\s+{var}:\s*)(\S+)", re.M),            # CI variables
    ]


def main() -> int:
    fix = "--fix" in sys.argv
    lock = load_lock()
    problems, found = [], set()
    for rel in FILES:
        path = ROOT / rel
        text = orig = path.read_text(encoding="utf-8")
        for var, want in lock.items():
            for pat in patterns(var):
                for m in pat.finditer(text):
                    found.add(var)
                    if m.group(2) != want:
                        problems.append(f"{rel}: {var}={m.group(2)}  (lock: {want})")
                if fix:
                    text = pat.sub(lambda m: m.group(1) + want + (m.group(3) if m.re.groups == 3 else ""), text)
        for m in re.finditer(r"^FROM\s+(\S+)", text, re.M):
            if not m.group(1).startswith("${"):
                problems.append(f"{rel}: FROM {m.group(1)} — образ не из lock (нужно ARG + FROM ${{VAR}})")
        if fix and text != orig:
            path.write_text(text, encoding="utf-8")
    unused = sorted(set(lock) - found)
    if unused:
        print("в lock, но нигде не используется:", ", ".join(unused))
    if fix:
        left = [p for p in problems if "FROM " in p]
        print(f"исправлено ссылок: {len(problems) - len(left)}")
        problems = left
    for p in problems:
        print("РАСХОЖДЕНИЕ", p)
    if not problems:
        print(f"OK: все ссылки на базовые образы совпадают с {LOCK.relative_to(ROOT)}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
