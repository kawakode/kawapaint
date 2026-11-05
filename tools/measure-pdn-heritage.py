#!/usr/bin/env python3
"""Measure how much of KawaPaint's source still comes from Paint.NET 3.36.

Usage:
    git fetch origin 3.36pdn
    python3 tools/measure-pdn-heritage.py [path-to-paint.net-3.36-sources]

Compares every tracked source file (.cs, .axaml, .js, .css, .html) of the current checkout with
the C# sources of the last MIT-licensed Paint.NET (3.36). Without an argument, the original
sources are read from this repository's 3.36pdn branch. See the "Transparency" section of
README.md for what each figure means.
"""
import collections, os, re, subprocess, sys, tarfile, tempfile, io

KP = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if len(sys.argv) > 1:
    PDN = sys.argv[1]
else:
    PDN = tempfile.mkdtemp(prefix='pdn336-')
    ref = next((r for r in ('origin/3.36pdn', '3.36pdn') if subprocess.run(
        ['git', '-C', KP, 'rev-parse', '-q', '--verify', r], capture_output=True).returncode == 0), None)
    if ref is None:
        sys.exit(__doc__)
    archive = subprocess.run(['git', '-C', KP, 'archive', ref, 'src'], capture_output=True, check=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(PDN, filter='data')
EXTS = ('.cs', '.axaml', '.js', '.css', '.html')
K = 3  # consecutive lines for a "copied block"
# Files whose header says their algorithms were ported/transcribed from Paint.NET 3.36.
PORTED = ['shared/KawaPaint.Engine/' + f for f in (
    'Effects.Artistic.cs', 'Effects.Blur.cs', 'Effects.Distort.cs', 'Effects.Noise.cs',
    'Effects.Photo.cs', 'Effects.Render.cs', 'Effects.Stylize.cs', 'PerlinNoise2D.cs',
    'ColorBgra.cs', 'Surface.cs', 'BlendMode.cs')]
TRIVIAL = re.compile(r'^(using [\w.]+;|namespace [\w.]+;?|#(region|endregion|if|endif|else).*|else|try|'
                     r'finally|default:|break;|continue;|return;|return (true|false|null|0|result);|'
                     r'base\.\w+\(\);|get;|set;|\{ get; set; \}|throw;)$')
STOP = {'this', 'var', 'int', 'double', 'float', 'byte', 'new', 'return', 'if', 'for', 'else',
        'private', 'public', 'static', 'readonly', 'const', 'void', 'in', 'out', 'ref'}


def read(path):
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.read().splitlines()


def norm(line):
    return re.sub(r'\s+', ' ', re.sub(r'//.*$', '', line.strip())).strip()


def significant(n):
    return len(n) >= 10 and not n.startswith(('/*', '*', '<!--')) and not TRIVIAL.match(n)


def tokens(line):
    return frozenset(t.lower() for t in re.findall(r'[A-Za-z_]\w*|\d+(?:\.\d+)?', re.sub(r'//.*$', '', line))
                     if t not in STOP)


pdn_lines, pdn_blocks, pdn_toks = set(), set(), set()
for root, _, names in os.walk(os.path.join(PDN, 'src')):
    for name in names:
        if name.endswith('.cs'):
            raw = read(os.path.join(root, name))
            sig = [n for n in map(norm, raw) if significant(n)]
            pdn_lines.update(sig)
            pdn_blocks.update(tuple(sig[i:i + K]) for i in range(len(sig) - K + 1))
            pdn_toks.update(t for t in map(tokens, raw) if len(t) >= 4)
pdn_toks = list(pdn_toks)
df = collections.Counter(t for s in pdn_toks for t in s)
index = collections.defaultdict(list)
for i, s in enumerate(pdn_toks):
    for t in sorted(s, key=df.__getitem__)[:int(0.3 * len(s)) + 1]:  # a >=70 % match must share one
        index[t].append(i)


def fuzzy_hit(s):
    cands = {i for t in s for i in index.get(t, ())}
    return any(len(s & pdn_toks[i]) / len(s | pdn_toks[i]) >= 0.7 for i in cands)


files = [f for f in subprocess.run(['git', '-C', KP, 'ls-files'], capture_output=True, text=True,
                                   check=True).stdout.splitlines() if f.endswith(EXTS)]
total = same_line = in_block = ported = fuzzy = fuzzy_total = 0
top = []
for f in files:
    raw = read(os.path.join(KP, f))
    sig = [n for n in map(norm, raw) if significant(n)]
    hit = [False] * len(sig)
    for i in range(len(sig) - K + 1):
        if tuple(sig[i:i + K]) in pdn_blocks:
            hit[i:i + K] = [True] * K
    total += len(sig)
    same_line += sum(n in pdn_lines for n in sig)
    in_block += sum(hit)
    ported += len(sig) if f in PORTED else 0
    if f.endswith('.cs'):
        toks = [t for t in map(tokens, raw) if len(t) >= 4]
        fuzzy_total += len(toks)
        fuzzy += sum(map(fuzzy_hit, toks))
    if sum(hit):
        top.append((sum(hit), f))

pct = lambda a, b: f'{100 * a / b:.1f} %'
head = subprocess.run(['git', '-C', KP, 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True).stdout.strip()
print(f'KawaPaint {head}: {len(files)} files, {total} significant lines')
print(f'Copied blocks ({K}+ identical consecutive lines): {in_block} ({pct(in_block, total)})')
print(f'Lines identical to a Paint.NET line:           {same_line} ({pct(same_line, total)})')
print(f'Lines similar to a Paint.NET line (C#, >=70%): {fuzzy}/{fuzzy_total} ({pct(fuzzy, fuzzy_total)})')
print(f'Lines in files ported from Paint.NET:          {ported} ({pct(ported, total)})')
for n, f in sorted(top, reverse=True):
    print(f'  {f}: {n} lines in copied blocks')
