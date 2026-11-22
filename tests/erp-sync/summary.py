"""Reads the sync's JSON log and checks its "sync summary" lines.

  summary.py LOG                       prints one line per entity
  summary.py LOG ENTITY CHECK...       exits 1 unless every check holds, e.g. created=10000 read<100 outcome=ok
"""
import json
import operator
import re
import sys


def summaries(path):
    found = {}
    with open(path, encoding="utf-8", errors="replace") as log:
        for line in log:
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            if entry.get("msg") == "sync summary":
                found[entry["entity"]] = entry
    return found


def show(entry):
    return " ".join(f"{k}={entry.get(k)}" for k in ("entity", "read", "retried", "created", "updated", "unchanged", "failed", "duration", "outcome"))


def main(argv):
    found = summaries(argv[1])
    if len(argv) == 2:
        for entry in found.values():
            print(show(entry))
        return 0
    entity, checks = argv[2], argv[3:]
    entry = found.get(entity)
    if entry is None:
        print(f"no summary for {entity}", file=sys.stderr)
        return 1
    ops = {"=": operator.eq, "<": operator.lt, ">": operator.gt}
    for check in checks:
        key, op, want = re.fullmatch(r"(\w+)([=<>])(.+)", check).groups()
        got = entry.get(key)
        if isinstance(got, int):
            want = int(want)
        if not ops[op](got, want):
            print(f"{entity}: {key} is {got}, want {op}{want}  ({show(entry)})", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
