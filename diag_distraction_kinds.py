"""
Read-only diagnostic: what attention-segment kinds make up today's data?

Opens the live Porta SQLite DB in READ-ONLY mode (cannot modify or lock it),
reads the most recent finalized buckets, and prints how many minutes fall under
each attention_segment_kind. This reveals exactly what is being counted as
distraction (e.g. is it dominated by 'focus_category_absent' from unrecognized
apps, vs. genuine social_media / gaming).

Run:   python diag_distraction_kinds.py
  (or) py diag_distraction_kinds.py
"""

import sqlite3, json, collections

DB = r"C:\ProgramData\PortaAgent\core-state.db"
RECENT_BUCKETS = 8000          # ~11h of active use; today's session(s)
BUCKET_SECONDS = 5

FOCUS = "focus"
AWAY = "away"
CONTEXT = "context_excursion"
NON_DISTRACTION = {FOCUS, AWAY, CONTEXT}

def main():
    uri = f"file:{DB}?mode=ro"
    try:
        con = sqlite3.connect(uri, uri=True, timeout=5)
    except sqlite3.Error as e:
        print(f"Could not open DB read-only: {e}")
        print(f"Check the path exists: {DB}")
        return

    cur = con.cursor()
    rows = cur.execute(
        "SELECT state_flags_json FROM state_buckets ORDER BY id DESC LIMIT ?",
        (RECENT_BUCKETS,),
    ).fetchall()
    con.close()

    def mins(n):
        return n * BUCKET_SECONDS / 60.0

    def breakdown(subset, label):
        counts = collections.Counter()
        for (sfj,) in subset:
            try:
                d = json.loads(sfj) if sfj else {}
            except Exception:
                d = {}
            kind = (d.get("attention_segment_kind") or FOCUS).strip().lower()
            counts[kind] += 1
        total = sum(counts.values())
        if total == 0:
            print(f"{label}: no buckets.\n")
            return
        distraction_buckets = sum(n for k, n in counts.items() if k not in NON_DISTRACTION)
        print(f"=== {label}: {total} buckets (~{mins(total)/60:.1f}h active) ===")
        print(f"{'attention_segment_kind':34}{'minutes':>9}{'share':>8}")
        print("-" * 51)
        for kind, n in counts.most_common():
            tag = "" if kind in NON_DISTRACTION else "  (distraction)"
            print(f"{kind:34}{mins(n):>9.1f}{n/total*100:>7.1f}%{tag}")
        print("-" * 51)
        print(f"{'TOTAL distraction':34}{mins(distraction_buckets):>9.1f}"
              f"{distraction_buckets/total*100:>7.1f}%\n")

    # rows are newest-first (ORDER BY id DESC), so rows[:N] is the most recent N.
    # The RECENT window shows post-deploy buckets clearly once you've used the new
    # build for a while, without dilution from older pre-deploy data.
    breakdown(rows[:1200], "RECENT (~last 1.5h of active use)")
    breakdown(rows, "FULL (last ~11h of active use)")

if __name__ == "__main__":
    main()
