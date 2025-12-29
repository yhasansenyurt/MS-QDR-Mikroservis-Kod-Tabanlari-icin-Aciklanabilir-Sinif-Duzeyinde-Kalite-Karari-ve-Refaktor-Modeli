# MS-QDR (Class-Level) Package

This package runs a Roslyn-based extractor over a C# solution and produces **class-level** quality decisions:
- **OK / WATCH / REFACTOR** per class (CQS score 0–100)
- Service information is used only for grouping/reporting (not as a decision model).

## Requirements
- Python 3.10+
- .NET SDK (dotnet)

## Run
From the folder that contains `run_ms_qdr.py` and the repo folder:

```bash
python run_ms_qdr.py --solution hvl-shopping/HavelsanShoppingApi.sln --repo-root hvl-shopping
```

Outputs are written under `./out/` by default.

## Outputs
- `out/raw_metrics.json` : raw extracted class metrics
- `out/dependencies.csv` : class-to-class dependency edges (used for FanIn/FanOut)
- `out/class_scores_detailed.csv` : **main report** (one column per feature + risks + decision)
- `out/refactor_candidates.csv` : only classes labelled **REFACTOR** (sorted worst-first)
- `out/service_decision_summary.csv` : per-service counts of OK/WATCH/REFACTOR (for discussion)

## Notes
If you already have `raw_metrics.json` and `dependencies.csv`, you can skip running the analyzer:

```bash
python run_ms_qdr.py --solution hvl-shopping/HavelsanShoppingApi.sln --repo-root hvl-shopping --skip-analyzer
```
