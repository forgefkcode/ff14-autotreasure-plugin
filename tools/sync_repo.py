"""Refresh the third-party entries of repo.json from the TC plugin repositories they come from.

The AutoTreasure repository lists everything the auto-treasure run needs, so a new player adds one URL. Only AutoTreasure is
hosted here; the other entries are copied from their own TC repositories as they are, download links included, so nothing
is re-uploaded and their updates show up after the next sync. Entries not in SOURCES (AutoTreasure) are kept unchanged.

Usage: python tools/sync_repo.py   (run by .github/workflows/sync-repo.yml every 6 hours)
"""
import json
import urllib.request

CYCLEAPPLE = "https://raw.githubusercontent.com/cycleapple/DalamudPlugins-TW-API13-Extended/main/repo.json"
LILASREPO = "https://raw.githubusercontent.com/lilasrepo/DalamudPlugins/main/pluginmaster.json"

# InternalName -> repository; the same builds the auto-treasure run is tested with
SOURCES = {
    "vnavmesh": CYCLEAPPLE,
    "Lifestream": CYCLEAPPLE,
    "RotationSolver": CYCLEAPPLE,
    "WrathCombo": CYCLEAPPLE,
    "BossModReborn": CYCLEAPPLE,
    "XivTreasureParty": CYCLEAPPLE,
    "BossMod": LILASREPO,  # the TC port the BossMod.Maps pack is built against (cycleapple's "BossMod" is another build)
}


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "ff14-autotreasure-plugin repo sync"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode("utf-8-sig"))


def main():
    with open("repo.json", encoding="utf-8") as f:
        old = json.load(f)
    ours = [m for m in old if m["InternalName"] not in SOURCES]
    upstream = {url: {m["InternalName"]: m for m in fetch(url)} for url in set(SOURCES.values())}
    theirs = []
    for name, url in SOURCES.items():
        m = upstream[url].get(name)
        if m is None:
            # keep the last known entry rather than dropping the plugin when the source repo changes
            m = next((o for o in old if o["InternalName"] == name), None)
            print(f"{name}: not found in {url}" + (", keeping the old entry" if m else ""))
        if m is not None:
            theirs.append(m)
    text = json.dumps(ours + theirs, ensure_ascii=False, indent=2) + "\n"
    with open("repo.json", encoding="utf-8") as f:
        changed = f.read() != text
    if changed:
        with open("repo.json", "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    for m in ours + theirs:
        print(f'{m["InternalName"]} {m["AssemblyVersion"]} API {m.get("DalamudApiLevel")}')
    print("repo.json updated" if changed else "repo.json unchanged")


if __name__ == "__main__":
    main()
