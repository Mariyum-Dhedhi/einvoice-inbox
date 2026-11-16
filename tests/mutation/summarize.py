"""Turns a Stryker JSON report into the small summary kept in tests/mutation/results (the full report is megabytes).

    python tests/mutation/summarize.py <mutation-report.json> <project> <commit> > tests/mutation/results/<date>-<project>.json

Standard library only. Counts per status, the score as Stryker computes it (killed + timeout over everything tested
plus not covered), and per file the killed and surviving mutants with the line and mutator of each survivor.
"""
import json
import os
import sys
from collections import Counter


def main(report_path: str, project: str, commit: str) -> None:
    with open(report_path, encoding="utf-8") as f:
        report = json.load(f)

    totals = Counter()
    files = {}
    for path, data in sorted(report["files"].items()):
        name = path.replace("\\", "/").split("/src/")[-1]
        lines = data["source"].splitlines()
        counts = Counter(m["status"] for m in data["mutants"])
        totals.update(counts)
        survivors = [
            {
                "line": m["location"]["start"]["line"],
                "status": m["status"],
                "mutator": m["mutatorName"],
                "code": lines[m["location"]["start"]["line"] - 1].strip()[:120],
            }
            for m in data["mutants"]
            if m["status"] in ("Survived", "NoCoverage")
        ]
        if counts.get("Killed", 0) or survivors:
            files[name] = {"killed": counts.get("Killed", 0) + counts.get("Timeout", 0), "survivors": survivors}

    detected = totals["Killed"] + totals["Timeout"]
    valid = detected + totals["Survived"] + totals["NoCoverage"]
    json.dump(
        {
            "project": project,
            "commit": commit,
            "tool": "dotnet-stryker 5.0.0 (tests/EInvoice.UnitTests/stryker-config.json)",
            "totals": dict(sorted(totals.items())),
            "score": round(100 * detected / valid, 2) if valid else None,
            "files": files,
        },
        sys.stdout,
        indent=1,
        ensure_ascii=False,
    )
    sys.stdout.write("\n")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
