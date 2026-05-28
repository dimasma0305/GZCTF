#!/usr/bin/env python3
"""
Generate a coverage-matrix demo repo for GZCTF repo-binding: one
challenge.yaml per (ChallengeType x ChallengeCategory) cell.
6 types x 12 categories = 72 challenges.

Output: a directory tree ready for `git init && gh repo create --push`.
The deployed copy lives at:
    https://github.com/dimasma0305/gzctf-mix-demo

Layout written under $OUT:
    .gzevent                    # event manifest (one game)
    README.md
    <Category>/<slug>/challenge.yaml   # 72 of these

Container-type rows reuse gzctf/echo-http:test so they stand up on any
deploy that already has the demo image; dynamic-type rows have a
placeholder flagTemplate (these are SHOWCASE entries, not playable).

Usage:
    python3 scripts/seed/gen-mix-repo.py /tmp/gzctf-mix-demo
    cd /tmp/gzctf-mix-demo && git init && gh repo create ... --push

Then register the resulting URL via the admin UI
(http://<host>:8080/admin/repo-bindings) and the background scanner
imports the event + every challenge.
"""
import sys, pathlib

OUT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/gzctf-mix-demo")

CATEGORIES = ["Misc","Crypto","Pwn","Web","Reverse","Blockchain","Forensics","Hardware","Mobile","PPC","AI","Pentest"]
TYPES = ["StaticAttachment","StaticContainer","DynamicAttachment","DynamicContainer","AttackDefense","KingOfTheHill"]

TITLE_TEMPLATES = {
    "StaticAttachment":  "{cat} Sampler",
    "StaticContainer":   "{cat} Service",
    "DynamicAttachment": "{cat} Per-Team Drop",
    "DynamicContainer":  "{cat} Per-Team Box",
    "AttackDefense":     "A&D — {cat}",
    "KingOfTheHill":     "KotH — {cat} Hill",
}

CAT_DESC = {
    "Misc":       "A bit of everything — read the prompt carefully.",
    "Crypto":     "Cryptographic puzzles; classical or modern.",
    "Pwn":        "Memory corruption / binary exploitation.",
    "Web":        "Web application security.",
    "Reverse":    "Reverse engineering — recover the algorithm.",
    "Blockchain": "Smart-contract or wallet puzzles.",
    "Forensics":  "Recover artifacts from data / disk / memory / pcap.",
    "Hardware":   "Embedded / firmware / hardware-adjacent.",
    "Mobile":     "Android / iOS app challenge.",
    "PPC":        "Programming + perf — solve under time pressure.",
    "AI":         "LLM / ML model attack-or-coax challenge.",
    "Pentest":    "Full chain — recon to root.",
}

TYPE_DESC = {
    "StaticAttachment":  "Download the file; the same flag applies to every team.",
    "StaticContainer":   "Shared service container — same target for every team.",
    "DynamicAttachment": "Per-team file with a per-team flag.",
    "DynamicContainer":  "Per-team container with a per-team flag.",
    "AttackDefense":     "Live A&D — patch your team's container, attack the others.",
    "KingOfTheHill":     "Single shared hill — race to plant your token in /koth/king.",
}

CONTAINER_IMG = "gzctf/echo-http:test"

GZEVENT = """# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/gzevent.schema.yaml
title: "GZCTF — Type×Category Mix Demo"
start: "2026-05-28T00:00:00Z"
end:   "2026-06-30T00:00:00Z"
hidden: false
summary: "Showcase event with one challenge per (type, category) cell — 72 total — wired up via repo-binding."
content: |
  This event is a coverage matrix: every ChallengeType crossed with every ChallengeCategory,
  one challenge each. The challenges themselves are placeholder/showcase entries so the
  player scoreboard, kind switcher, and category bands render with content in every cell.
acceptWithoutReview: true
practiceMode: true
teamMemberCountLimit: 0
containerCountLimit: 5
bloodBonus: 50
ad:
  tickSeconds: 60
  flagLifetimeTicks: 5
  warmupSeconds: 60
  resetCooldownMinutes: 5
"""

README = """# GZCTF mix demo

Repo-bound seed event for **GZCTF**: one challenge for every
(ChallengeType × ChallengeCategory) cell — 6 types × 12 categories = **72 challenges**.

Wire this URL into `/admin/repo-bindings` on a GZCTF deployment and the
background scanner will create the event + import every `challenge.yaml`
under each category folder.

Generated showcase entries — container-type rows reuse `gzctf/echo-http:test`,
dynamic types have placeholder flag templates. Useful for UI/scoreboard
parity testing, not as a real CTF.

Regenerated from `scripts/seed/gen-mix-repo.py` in the GZCTF repo.
"""


def render(t: str, cat: str) -> str:
    title = TITLE_TEMPLATES[t].format(cat=cat)
    desc = f"{CAT_DESC[cat]}\n\n{TYPE_DESC[t]}"
    out = []
    out.append("# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml")
    out.append("")
    out.append(f'name: "{title}"')
    out.append('author: "GZCTF Mix Demo"')
    out.append('description: |')
    for line in desc.splitlines():
        out.append(f"  {line}")
    out.append(f'category: "{cat}"')
    out.append(f'type: "{t}"')
    out.append('value: 0' if t in ("AttackDefense","KingOfTheHill") else 'value: 200')
    out.append('minScoreRate: 0.25')
    out.append('difficulty: 5')

    if t in ("StaticAttachment","StaticContainer"):
        out.append('flags:')
        out.append(f'  - "FINDIT{{{t.lower()}_{cat.lower()}_static}}"')

    if t in ("DynamicAttachment","DynamicContainer"):
        out.append(f'flagTemplate: "FINDIT{{{t.lower()}_{cat.lower()}_[GUID]}}"')

    if t in ("StaticContainer","DynamicContainer","AttackDefense","KingOfTheHill"):
        out.append('container:')
        out.append(f'  containerImage: "{CONTAINER_IMG}"')
        out.append('  exposePort: 80')
        out.append('  memoryLimit: 128')
        out.append('  cpuCount: 1')
        out.append('  storageLimit: 256')

    if t == "AttackDefense":
        out.append('ad:')
        out.append('  allowEgress: true')
        out.append('  allowSelfReset: true')

    out.append("")
    return "\n".join(out)


def slugify(s: str) -> str:
    s = s.lower().replace("&", "and").replace(" — ", " ").replace("—", "").replace(" ", "-").replace("/", "-")
    return "".join(c for c in s if c.isalnum() or c == "-")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / ".gzevent").write_text(GZEVENT)
    (OUT / "README.md").write_text(README)
    n = 0
    for cat in CATEGORIES:
        for t in TYPES:
            slug = slugify(TITLE_TEMPLATES[t].format(cat=cat))
            d = OUT / cat / slug
            d.mkdir(parents=True, exist_ok=True)
            (d / "challenge.yaml").write_text(render(t, cat))
            n += 1
    print(f"wrote {n} challenge.yaml files under {OUT}")


if __name__ == "__main__":
    main()
