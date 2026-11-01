"""Reproducible source inventory and traceable coverage ledger. Inventory is not semantic review."""
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SS14 = Path("C:/ss14/space-stories-cm14")
WORKSHOP = Path("C:/Steam/steamapps/workshop/content/602960")
SOURCES = {"GM Tools": WORKSHOP / "2829470717", "GM Menu": WORKSHOP / "3699710631", "SS14": SS14}
inventory = []
features = []


def add_feature(source, path, text, match, kind, label):
    features.append({"source": source, "path": str(path), "line": text.count("\n", 0, match.start()) + 1, "kind": kind, "label": label,
                     "review": "inventory_only", "implementation": "not_claimed", "applicability": "requires_semantic_review", "architecture": "undecided"})


for source, directory in SOURCES.items():
    paths = []
    if source == "SS14":
        for folder in ("Content.Client", "Content.Server", "Content.Shared", "RobustToolbox/Robust.Client", "RobustToolbox/Robust.Server", "RobustToolbox/Robust.Shared"):
            for path in (directory / folder).rglob("*.cs"):
                if any(p in ("bin", "obj", ".git") for p in path.parts):
                    continue
                text = path.read_text(encoding="utf-8-sig", errors="replace")
                if any(part in str(path) for part in ("Administration", "ViewVariables", "/Admin/", "\\Admin\\")) or "[AdminCommand(" in text or "[AnyCommand]" in text:
                    paths.append((path, text))
    else:
        for extension in ("*.lua", "*.cs"):
            paths.extend((path, path.read_text(encoding="utf-8-sig", errors="replace")) for path in directory.rglob(extension))
    for path, text in sorted(paths):
        inventory.append({"source": source, "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "lines": len(text.splitlines()), "review": "inventory_only"})
        if source == "GM Tools":
            # Ignore Lua block comments before identifying declarations; preserve line numbers.
            code = re.sub(r"--\[\[.*?\]\]", lambda m: "\n" * m.group().count("\n"), text, flags=re.S)
            for match in re.finditer(r'command\.AddCommand\(\s*"([^"]+)"', code):
                add_feature(source, path, code, match, "command", match[1])
            for match in re.finditer(r'module\.newAction\(\s*"([^"]+)"\s*,\s*"([^"]+)"', code):
                add_feature(source, path, code, match, "device_action", match[1] + "." + match[2])
        elif source == "GM Menu":
            if path.name == "action_defs.lua":
                for match in re.finditer(r"\bid\s*=\s*['\"]([^'\"]+)['\"]", text):
                    add_feature(source, path, text, match, "declared_action", match[1])
            for match in re.finditer(r"RegisterServerActionHandler\(\s*['\"]([^'\"]+)['\"]", text):
                add_feature(source, path, text, match, "server_handler", match[1])
        else:
            for match in re.finditer(r'(?:string\s+Command\s*=>|\[ConsoleCommand\()\s*"([^"]+)"', text):
                add_feature(source, path, text, match, "command", match[1])
            if "AdminVerbSystem" in path.name:
                for match in re.finditer(r'Text\s*=\s*(?:Loc.GetString\()?"([^"]+)"', text):
                    add_feature(source, path, text, match, "context_verb", match[1])

versions = {}
for source in ("GM Tools", "GM Menu"):
    manifest = SOURCES[source] / "filelist.xml"
    versions[source] = {"path": str(SOURCES[source]), **ET.parse(manifest).getroot().attrib}
versions["SS14"] = {"path": str(SS14), "commit": subprocess.check_output(["git", "-C", str(SS14), "rev-parse", "HEAD"], text=True).strip()}
output = ROOT / "Docs"
output.mkdir(exist_ok=True)
(output / "gm-source-inventory.json").write_text(json.dumps({"versions": versions, "files": inventory, "detectedFeatures": features,
    "limits": ["inventory_only means declarations were indexed, not a complete semantic review", "no GM Menu source, class, architecture, interface or asset was copied into production", "runtime behavior has not been verified by launching either game"]}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
counts = {source: {"files": sum(f["source"] == source for f in inventory), "declarations": sum(f["source"] == source for f in features)} for source in SOURCES}
print(json.dumps(counts, ensure_ascii=False))
