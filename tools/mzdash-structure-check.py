#!/usr/bin/env python3
"""Validate emitted .mzdash files against the structure of factory dashboards.

Learns the per-type envelope (which keys each node type carries, and which keys each
sub-block carries) from a reference tree of known-good .mzdash files, then checks that
generated files match it. A missing `borderStyle` or a stray `innerShadow` renders as a
silently blank or mis-drawn widget on the wheel rather than an error, so this is the
cheapest place to catch an emitter regression.

  usage: mzdash-structure-check.py <generated-dir> [--reference ~/dashes] [--quiet]

Exit code 0 when every generated node matches a known envelope, 1 otherwise.
"""
import argparse
import collections
import json
import os
import re
import sys
import glob

# Keys that legitimately vary per node rather than per type.
VALUE_BLOCKS = {'general', 'effect', 'borderStyle', 'text', 'textEffect', 'image',
                'linearGauge', 'circularGauge', 'ellipse', 'window'}

# Verbatim from Telemetry/Dashboard/DashboardProfileStore.cs — the two patterns the
# plugin scrapes a .mzdash with to decide which channels a dashboard needs. If an
# emitted Telemetry.get() does not match these, the wheel renders the widget but the
# plugin never streams the channel, so it sits at its design-time value forever.
TELEMETRY_GET_RE = re.compile(r'Telemetry\.get\(\\?["\'](v1/gameData/[^"\'\\]+)\\?["\']\)')
RAW_URL_RE = re.compile(r'v1/gameData/[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)*')

# Dashboard canvases MOZA ships. From Dashboard Studio's own idealDeviceInfoMap plus the
# sizes observed across PitHouse's 47 factory dashboards — 1449x720 appears in five of
# them but has no entry in MOZA's map, so both sources are needed.
KNOWN_CANVASES = {
    (480, 480),     # VGS
    (847, 480),     # ESSENZA SCV12 / FSR V2
    (1417, 700),    # Porsche Mission R
    (780, 248),     # CS Pro / KS Pro / Mustang GTD
    (340, 340),     # W22
    (1280, 720),    # CM2
    (1449, 720),    # observed in factory dashboards, absent from MOZA's device map
}


def learn(reference_dir):
    """type -> (set of top-level keys seen, {block -> set of keys seen})."""
    top = collections.defaultdict(set)
    sub = collections.defaultdict(lambda: collections.defaultdict(set))
    files = glob.glob(os.path.join(reference_dir, '*', '*.mzdash'))
    for path in files:
        with open(path, encoding='utf-8') as fh:
            doc = json.load(fh)

        def walk(node):
            if isinstance(node, dict):
                kind = node.get('type')
                if isinstance(kind, str) and kind.endswith('.qml'):
                    top[kind] |= set(node)
                    for key, value in node.items():
                        if key in VALUE_BLOCKS and isinstance(value, dict):
                            sub[kind][key] |= set(value)
                for value in node.values():
                    walk(value)
            elif isinstance(node, list):
                for value in node:
                    walk(value)

        walk(doc)
    return top, sub, len(files)


def check(path, top, sub, catalog):
    """Return a list of complaint strings for one generated file."""
    problems = []
    with open(path, encoding='utf-8') as fh:
        doc = json.load(fh)

    seen_ids = set()

    def walk(node, where):
        if isinstance(node, dict):
            kind = node.get('type')
            if isinstance(kind, str) and kind.endswith('.qml'):
                where = f'{where}/{kind}'
                if kind not in top:
                    problems.append(f'{where}: no factory dashboard uses this type')
                else:
                    unknown = set(node) - top[kind]
                    if unknown:
                        problems.append(f'{where}: keys not seen on this type in factory '
                                        f'files: {sorted(unknown)}')
                    for key, value in node.items():
                        if key in VALUE_BLOCKS and isinstance(value, dict):
                            extra = set(value) - sub[kind].get(key, set())
                            if extra:
                                problems.append(
                                    f'{where}.{key}: unknown keys {sorted(extra)}')

                # ids must be unique across the document; the wheel indexes by them.
                node_id = node.get('id')
                if node_id is not None:
                    if node_id in seen_ids:
                        problems.append(f'{where}: duplicate id {node_id}')
                    seen_ids.add(node_id)

                binding = node.get('binding')
                if isinstance(binding, dict):
                    for target, spec in binding.items():
                        if not isinstance(spec, dict):
                            problems.append(f'{where}: binding {target} is not an object')
                            continue
                        if spec.get('type') != 'METHOD_CHAINING':
                            problems.append(
                                f'{where}: binding {target} type={spec.get("type")!r}, '
                                'factory files only ever use METHOD_CHAINING')
                        methods = spec.get('methods')
                        if not isinstance(methods, list) or not 1 <= len(methods) <= 2:
                            problems.append(
                                f'{where}: binding {target} has '
                                f'{len(methods) if isinstance(methods, list) else "?"} '
                                'methods; factory files use 1 or 2')

            for value in node.values():
                walk(value, where)
        elif isinstance(node, list):
            for value in node:
                walk(value, where)

    walk(doc, os.path.basename(path))

    root_type = doc.get('type')
    if root_type != 'Window.qml':
        problems.append(f'root type is {root_type!r}, expected Window.qml')
    if doc.get('id') != 0:
        problems.append(f'root id is {doc.get("id")!r}, expected 0')
    if doc.get('version') != '1.1.1':
        problems.append(f'version is {doc.get("version")!r}, expected 1.1.1')

    general = doc.get('general') or {}
    size = (general.get('width'), general.get('height'))
    if size not in KNOWN_CANVASES:
        problems.append(f'canvas is {size[0]}x{size[1]}, not a MOZA display size '
                        f'({", ".join(f"{w}x{h}" for w, h in sorted(KNOWN_CANVASES))})')

    problems.extend(check_plugin_reader(path, doc, catalog))
    return problems


def bound_channels(doc):
    """Channel URLs the document's bindings actually read."""
    urls = set()

    def walk(node):
        if isinstance(node, dict):
            binding = node.get('binding')
            if isinstance(binding, dict):
                for spec in binding.values():
                    if isinstance(spec, dict):
                        for step in spec.get('methods') or []:
                            if isinstance(step, str):
                                urls.update(TELEMETRY_GET_RE.findall(step))
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(doc)
    return urls


def check_plugin_reader(path, doc, catalog):
    """Two properties the emitter must hold for bindings to actually carry data.

    1. Every bound URL exists in Data/Telemetry.json. The wheel only streams channels
       from its catalog, so a typo'd URL renders a widget that reads NaN forever with
       nothing in any log to say why.
    2. JSON escaping does not defeat the plugin's scrape. DashboardProfileStore reads
       the raw file text, where the emitted `Telemetry.get("...")` appears as
       `Telemetry.get(\\"...\\")`; its regex allows for that, and this confirms the
       pairing still holds. RAW_URL_RE is deliberately not accepted as a substitute --
       it stops at the first character outside [A-Za-z0-9_/], silently truncating a
       `&unit=` channel to a different, real one.
    """
    with open(path, encoding='utf-8') as fh:
        text = fh.read()

    scraped = set(TELEMETRY_GET_RE.findall(text))
    bound = bound_channels(doc)

    problems = []
    if catalog:
        for url in sorted(bound - catalog):
            problems.append(f'channel {url} is bound but is not in Telemetry.json')
    for url in sorted(bound - scraped):
        problems.append(f'channel {url} is bound but JSON escaping hides it '
                        'from the plugin reader')
    return problems


def load_catalog(repo_root):
    """Channel URLs from Data/Telemetry.json, or an empty set when it isn't there."""
    path = os.path.join(repo_root, 'Data', 'Telemetry.json')
    try:
        with open(path, encoding='utf-8') as fh:
            doc = json.load(fh)
    except (OSError, ValueError):
        return set()
    return {s.get('url') for s in doc.get('sectors', []) if isinstance(s, dict)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('generated')
    parser.add_argument('--reference', default=os.path.expanduser('~/dashes'))
    parser.add_argument('--quiet', action='store_true')
    args = parser.parse_args()

    if not os.path.isdir(args.reference):
        print(f'reference tree not found: {args.reference}', file=sys.stderr)
        return 2

    catalog = load_catalog(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    top, sub, ref_count = learn(args.reference)
    if ref_count == 0:
        print(f'no .mzdash files under {args.reference}', file=sys.stderr)
        return 2
    print(f'learned {len(top)} node types from {ref_count} factory dashboards')

    files = sorted(glob.glob(os.path.join(args.generated, '*', '*.mzdash')))
    if not files:
        print(f'no generated .mzdash under {args.generated}', file=sys.stderr)
        return 2

    bad = 0
    total_problems = collections.Counter()
    for path in files:
        try:
            problems = check(path, top, sub, catalog)
        except Exception as exc:                      # noqa: BLE001 - report, don't abort
            problems = [f'could not be read: {exc}']
        if problems:
            bad += 1
            print(f'\n{os.path.basename(path)}')
            for problem in problems[:10]:
                print(f'  {problem}')
                total_problems[problem.split(':', 1)[-1].strip()[:70]] += 1
            if len(problems) > 10:
                print(f'  ... and {len(problems) - 10} more')
        elif not args.quiet:
            print(f'ok  {os.path.basename(path)}')

    print(f'\n{len(files) - bad}/{len(files)} files structurally match factory dashboards')
    if total_problems:
        print('\nmost common problems:')
        for problem, count in total_problems.most_common(10):
            print(f'  {count:4d}  {problem}')
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
