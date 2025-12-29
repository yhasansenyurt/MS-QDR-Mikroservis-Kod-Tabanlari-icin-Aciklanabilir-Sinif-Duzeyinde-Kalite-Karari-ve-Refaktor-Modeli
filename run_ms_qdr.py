#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import subprocess
import re
from collections import defaultdict, Counter
from pathlib import Path
from statistics import median

def _get(d: dict, *keys, default=None):
    for k in keys:
        if k in d:
            return d[k]
    return default

def get_metrics_dict(c: dict) -> dict:
    m = _get(c, "metrics", "Metrics", default={})
    return m if isinstance(m, dict) else {}

def norm_class(c: dict) -> dict:
    return {
        "id": _get(c, "id", "Id", default=""),
        "name": _get(c, "name", "Name", default=""),
        "namespace": _get(c, "namespace", "Namespace", default=""),
        "project": _get(c, "project", "Project", default=""),
        "service": _get(c, "service", "Service", default="Unknown"),
        "filePath": _get(c, "filePath", "FilePath", default=""),
        "metrics": get_metrics_dict(c),
    }


def mad(values):
    if not values:
        return 0.0
    m = median(values)
    return median([abs(v - m) for v in values])

def sigmoid(x: float) -> float:
    if x < -30:
        return 0.0
    if x > 30:
        return 1.0
    return 1.0 / (1.0 + math.exp(-x))

def robust_risk(x: float, med: float, mad_val: float, alpha: float = 1.0) -> float:
    denom = 1.4826 * mad_val + 1e-9
    z = (x - med) / denom
    return sigmoid(alpha * z)

def avg(xs):
    return sum(xs) / len(xs) if xs else 0.0

def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--solution", required=True, help="Path to .sln")
    p.add_argument("--repo-root", required=True, help="Repo root folder (used for path normalization)")
    p.add_argument("--out", default="out", help="Output folder")
    p.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    p.add_argument("--msbuild-path", default=None, help="Explicit MSBuild path (e.g., C:\\Program Files\\dotnet\\sdk\\10.0.101). If omitted, auto-detect from dotnet --info.")
    p.add_argument("--skip-analyzer", action="store_true", help="Skip running Roslyn analyzer and only score existing outputs")
    p.add_argument("--csv-delimiter", default=";", help="CSV delimiter (default ';' for Excel TR). Use ',' if you prefer comma-separated.")
    return p.parse_args()

def detect_msbuild_path(dotnet_exe: str) -> str | None:
    try:
        out = subprocess.check_output([dotnet_exe, "--info"], text=True, stderr=subprocess.STDOUT, encoding="utf-8", errors="ignore")
    except Exception:
        return None

    m = re.search(r"^\s*Base Path:\s*(.+)$", out, flags=re.MULTILINE)
    if not m:
        return None
    base = m.group(1).strip().strip('"')
    return base.rstrip("/\\")


def main():
    args = parse_args()

    script_dir = Path(__file__).resolve().parent
    tool_dir = script_dir / "roslyn_analyzer"
    csproj = tool_dir / "RoslynAnalyzer.csproj"

    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    out_json = out_dir / "raw_metrics.json"
    out_deps = out_dir / "dependencies.csv"

    if not args.skip_analyzer:
        subprocess.check_call([args.dotnet, "build", str(csproj), "-c", "Release"], cwd=str(tool_dir))

        msbuild_path = args.msbuild_path or detect_msbuild_path(args.dotnet)
        msbuild_path = msbuild_path.strip() if isinstance(msbuild_path, str) and msbuild_path.strip() else None
        subprocess.check_call([
            args.dotnet, "run", "-c", "Release", "--project", str(csproj), "--",
            "--solution", str(Path(args.solution).resolve()),
            "--repoRoot", str(Path(args.repo_root).resolve()),
            "--outJson", str(out_json),
            "--outDeps", str(out_deps),
            *(["--msbuildPath", msbuild_path] if msbuild_path else []),
        ], cwd=str(tool_dir))

    data = json.loads(out_json.read_text(encoding="utf-8"))
    classes = [norm_class(x) for x in data.get("classes", [])]

    if not classes:
        raise SystemExit("No classes found in raw_metrics.json")

    fanin = defaultdict(int)
    fanout = defaultdict(int)
    if out_deps.exists():
        with out_deps.open("r", encoding="utf-8") as f:
            r = csv.DictReader(f)
            for row in r:
                s = row.get("sourceClassId") or row.get("SourceClassId")
                t = row.get("targetClassId") or row.get("TargetClassId")
                if not s or not t:
                    continue
                fanout[s] += 1
                fanin[t] += 1

    base_metrics = ["LOC","NOM","NOF","WMC","RFC","CBO","Cyclomatic","LayerViolations","CycleInvolvement"]
    derived_metrics = ["FanIn","FanOut","Instability"]

    all_vals = {m: [] for m in base_metrics + derived_metrics}
    for c in classes:
        cid = c["id"]
        mx = c["metrics"]

        for m in base_metrics:
            all_vals[m].append(float(mx.get(m, 0.0)))

        fi = float(fanin.get(cid, 0))
        fo = float(fanout.get(cid, 0))
        all_vals["FanIn"].append(fi)
        all_vals["FanOut"].append(fo)
        denom = fi + fo
        all_vals["Instability"].append(fo / denom if denom > 0 else 0.0)

    stats = {m: (median(all_vals[m]), mad(all_vals[m])) for m in all_vals.keys()}

    class_rows = []
    for c in classes:
        cid = c["id"]
        mx = c["metrics"]
        mets = {m: float(mx.get(m, 0.0)) for m in base_metrics}
        mets["FanIn"] = float(fanin.get(cid, 0))
        mets["FanOut"] = float(fanout.get(cid, 0))
        denom = mets["FanIn"] + mets["FanOut"]
        mets["Instability"] = mets["FanOut"] / denom if denom > 0 else 0.0


        r_loc = robust_risk(mets.get("LOC", 0.0), *stats["LOC"])
        r_nom = robust_risk(mets.get("NOM", 0.0), *stats["NOM"])
        r_nof = robust_risk(mets.get("NOF", 0.0), *stats["NOF"])

        r_wmc = robust_risk(mets.get("WMC", 0.0), *stats["WMC"])
        r_rfc = robust_risk(mets.get("RFC", 0.0), *stats["RFC"])
        r_cyc = robust_risk(mets.get("Cyclomatic", 0.0), *stats["Cyclomatic"])

        r_cbo = robust_risk(mets.get("CBO", 0.0), *stats["CBO"])
        r_fout = robust_risk(mets.get("FanOut", 0.0), *stats["FanOut"])
        r_inst = robust_risk(mets.get("Instability", 0.0), *stats["Instability"])

        r_layer = robust_risk(mets.get("LayerViolations", 0.0), *stats["LayerViolations"])
        r_cycle = robust_risk(mets.get("CycleInvolvement", 0.0), *stats["CycleInvolvement"])

        R_size = avg([r_loc, r_nom, r_nof])
        R_complexity = avg([r_wmc, r_rfc, r_cyc])
        R_coupling = avg([r_cbo, r_fout, r_inst])
        R_architecture = avg([r_layer, r_cycle])

        R_class = 0.20 * R_size + 0.25 * R_complexity + 0.35 * R_coupling + 0.20 * R_architecture
        CQS = 100.0 * (1.0 - R_class)

        class_rows.append({
            "Service": c["service"],
            "Project": c["project"],
            "Namespace": c["namespace"],
            "ClassName": c["name"],
            "ClassId": cid,
            "FilePath": c["filePath"],
            **{m: mets[m] for m in base_metrics + derived_metrics},
            "R_size": R_size,
            "R_complexity": R_complexity,
            "R_coupling": R_coupling,
            "R_architecture": R_architecture,
            "CQS": CQS,
        })

    cqs_vals = sorted([r["CQS"] for r in class_rows])
    def percentile(p):
        if not cqs_vals:
            return 0.0
        k = int(round((len(cqs_vals)-1) * p))
        return cqs_vals[max(0, min(len(cqs_vals)-1, k))]

    p20 = percentile(0.20)
    p50 = percentile(0.50)

    action_map = {
        "size": "Split large classes, extract cohesive responsibilities, reduce method/field count.",
        "complexity": "Decompose complex methods, reduce branching, introduce strategy/polymorphism where suitable.",
        "coupling": "Reduce dependencies via DI + interfaces, facades, avoid reaching into many modules; extract smaller components.",
        "architecture": "Fix layer violations (move code to correct layer), break cycles via interfaces/events, enforce boundaries.",
    }

    for r in class_rows:

        if r["CQS"] <= p20 or r["R_coupling"] >= 0.75 or r["R_architecture"] >= 0.70:
            decision = "REFACTOR"
        elif r["CQS"] <= p50 or r["R_complexity"] >= 0.70:
            decision = "WATCH"
        else:
            decision = "OK"

        causes = [
            ("size", r["R_size"]),
            ("complexity", r["R_complexity"]),
            ("coupling", r["R_coupling"]),
            ("architecture", r["R_architecture"]),
        ]
        causes.sort(key=lambda x: x[1], reverse=True)
        primary = causes[0][0]
        r["Decision"] = decision
        r["PrimaryCause"] = primary
        r["RecommendedAction"] = action_map.get(primary, "")


    detailed_csv = out_dir / "class_scores_detailed.csv"
    cols = [
        "Service","Project","Namespace","ClassName","ClassId","FilePath",
        *base_metrics,*derived_metrics,
        "R_size","R_complexity","R_coupling","R_architecture",
        "CQS","Decision","PrimaryCause","RecommendedAction"
    ]

    for r in class_rows:
        for k in ["R_size", "R_complexity", "R_coupling", "R_architecture", "CQS",
                "LOC","NOM","NOF","WMC","RFC","CBO","Cyclomatic","LayerViolations","CycleInvolvement",
                "FanIn","FanOut","Instability"]:
            if k in r and isinstance(r[k], (int, float)):
                r[k] = f"{r[k]:.2f}"

    with detailed_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, delimiter=args.csv_delimiter, lineterminator="\n")
        w.writeheader()
        for r in class_rows:
            w.writerow({k: r.get(k, "") for k in cols})


    ref_csv = out_dir / "refactor_candidates.csv"
    ref_rows = [r for r in class_rows if r["Decision"] == "REFACTOR"]
    ref_rows.sort(key=lambda x: x["CQS"])
    with ref_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, delimiter=args.csv_delimiter, lineterminator="\n")
        w.writeheader()
        for r in ref_rows:
            w.writerow({k: r.get(k, "") for k in cols})


    summary_csv = out_dir / "service_decision_summary.csv"
    by_service = defaultdict(list)
    for r in class_rows:
        by_service[r["Service"]].append(r["Decision"])

    sum_cols = ["Service","TotalClasses","OK_Count","WATCH_Count","REFACTOR_Count","OK_Ratio","REFACTOR_Ratio"]
    sum_rows = []
    for svc, decs in sorted(by_service.items(), key=lambda x: x[0].lower()):
        c = Counter(decs)
        total = len(decs)
        ok = c.get("OK",0)
        watch = c.get("WATCH",0)
        ref = c.get("REFACTOR",0)
        sum_rows.append({
            "Service": svc,
            "TotalClasses": total,
            "OK_Count": ok,
            "WATCH_Count": watch,
            "REFACTOR_Count": ref,
            "OK_Ratio": (ok/total) if total else 0.0,
            "REFACTOR_Ratio": (ref/total) if total else 0.0,
        })

    with summary_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=sum_cols, delimiter=args.csv_delimiter, lineterminator="\n")
        w.writeheader()
        for r in sum_rows:
            w.writerow(r)

    print(f"[OK] CSV delimiter: {args.csv_delimiter!r} (Excel TR usually needs ';')")
    print("[OK] Wrote:", detailed_csv)
    print("[OK] Wrote:", ref_csv)
    print("[OK] Wrote:", summary_csv)
    print("[OK] Raw outputs:", out_json, out_deps)

if __name__ == "__main__":
    main()
