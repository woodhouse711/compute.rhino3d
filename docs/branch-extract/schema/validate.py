"""Oracle O-schema: validate the frozen contract and its fixtures.

    py -3.12 docs/branch-extract/schema/validate.py
    py -3.12 docs/branch-extract/schema/validate.py <file.json> [<file.json> ...]

With no arguments, validates the checked-in fixtures (the contract regression).
With arguments, validates those documents instead -- this is how real extractor
output gets held to the same bar as the fixtures, rather than the far weaker
"is it parseable JSON" check.

Exit 0 = pass. Run this before and after any schema edit; it is the only thing
standing between a contract change and a silent downstream break.

Requires: pip install jsonschema
"""
import json
import pathlib
import sys

from jsonschema import Draft202012Validator

HERE = pathlib.Path(__file__).parent
SCHEMA = HERE / "branch-extract-1.0.schema.json"
FIXTURES = sorted(HERE.glob("fixture-*.json"))

# Contract invariants that JSON Schema cannot express on its own. Each is a rule
# the pipeline exists to enforce, so each is checked mechanically rather than by
# reviewer discipline.
def semantic_checks(name, doc):
    problems = []

    for i, p in enumerate(doc.get("panels", [])):
        where = f"{name} panels[{i}]"
        errs = p.get("field_errors") or {}

        # A null with no recorded reason is a schema violation, not a value.
        for field in ("weight_kg", "volume_m3", "net_area_sqft", "species",
                      "grade", "density_assigned_kg_per_m3"):
            if p.get(field) is None and field not in errs:
                problems.append(f"{where}: {field} is null with no field_errors entry")

        # The sentinel must never be emitted as if it were a material.
        if p.get("material_name") == "Unassigned":
            problems.append(f"{where}: literal 'Unassigned' emitted as a material name")

        # Derived descriptors must be null when their native input is null.
        cx = p.get("complexity") or {}
        if cx.get("penetration_area_sqft") is not None:
            if p.get("gross_area_sqft") is None or p.get("net_area_sqft") is None:
                problems.append(f"{where}: penetration_area_sqft derived from a null atomic")

        # Weight and its two densities must not silently disagree.
        da, di = p.get("density_assigned_kg_per_m3"), p.get("density_implied_kg_per_m3")
        if da is not None and di is not None:
            disagrees = abs(da - di) > 1.0
            if disagrees and not p.get("density_discrepancy"):
                problems.append(f"{where}: assigned {da} vs implied {di} but density_discrepancy is not set")

    # 'ok' is only honest when nothing went wrong.
    ex = doc.get("extraction", {})
    summary = doc.get("summary") or {}
    if ex.get("status") == "ok" and summary.get("panels_with_errors", 0) > 0:
        problems.append(f"{name}: status 'ok' with {summary['panels_with_errors']} panels carrying errors")
    if ex.get("status") == "ok" and ex.get("types_recognised") == {}:
        problems.append(f"{name}: status 'ok' with an empty census — should be 'no_branch_content'")

    # Truncation must say how much was lost.
    if ex.get("status") == "partial_truncated" and not (ex.get("dropped") or {}).get("count"):
        problems.append(f"{name}: partial_truncated without a dropped count")

    return problems


def main(argv=None):
    argv = list(argv if argv is not None else sys.argv[1:])

    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    print(f"schema      : {SCHEMA.name} — valid draft-2020-12")

    validator = Draft202012Validator(schema)
    failed = False

    if argv:
        targets = [pathlib.Path(a) for a in argv]
        missing = [t for t in targets if not t.is_file()]
        if missing:
            for t in missing:
                print(f"!! not a file: {t}")
            return 1
    else:
        targets = FIXTURES
        if not targets:
            print("!! no fixtures found")
            return 1

    for path in targets:
        doc = json.loads(path.read_text(encoding="utf-8"))
        errors = sorted(validator.iter_errors(doc), key=lambda e: list(e.path))
        problems = semantic_checks(path.name, doc)

        if not errors and not problems:
            print(f"  {path.name}: VALID")
            continue

        failed = True
        print(f"  {path.name}: {len(errors)} schema error(s), {len(problems)} semantic problem(s)")
        for e in errors[:10]:
            print(f"     schema   at {list(e.path)}: {e.message[:160]}")
        for p in problems[:10]:
            print(f"     semantic {p}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
