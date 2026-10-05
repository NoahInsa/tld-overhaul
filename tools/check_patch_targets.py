"""Static sanity check for Harmony patch targets (no game needed).

For every [HarmonyPatch(typeof(X), nameof(X.Y))] in src/, look up X.cs in the decompiled interop stubs and report:
  * AMBIGUOUS  - Y is overloaded (Harmony throws at load unless an argument list is given)
  * MISSING    - no such method
  * cc=N       - the stub's [CallerCount(N)]; 0 on a non-Unity-message method means it may be inlined (patch may never fire)
Usage: python tools/check_patch_targets.py [decompiled_root]
"""
import os, re, sys

root = sys.argv[1] if len(sys.argv) > 1 else r'C:\TLD'
src = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')
files = {}
for base in ('Assembly-CSharp', 'Assembly-CSharp-firstpass'):
    for dp, _, fn in os.walk(os.path.join(root, base)):
        for f in fn:
            if f.endswith('.cs'): files.setdefault(f[:-3], os.path.join(dp, f))

def methods(cls, name):
    p = files.get(cls)
    if not p: return None
    L = open(p, encoding='utf-8-sig').read().split('\n')
    out = []
    for i, l in enumerate(L):
        if re.search(r'\b' + re.escape(name) + r'\(', l) and re.search(r'^\s*(public|private|protected|internal)\b', l) and 'Native' not in l and 'static ' + cls not in l:
            cc = '?'
            for j in range(max(0, i - 4), i):
                m = re.search(r'CallerCount\((\d+)\)', L[j])
                if m: cc = m.group(1)
            out.append((cc, l.strip()))
    return out

def params_of(sig):
    m = re.search(r'\((.*)\)', sig)
    if not m: return []
    out = []
    depth = 0; cur = ''
    for ch in m.group(1):
        if ch in '<(': depth += 1
        if ch in '>)': depth -= 1
        if ch == ',' and depth == 0: out.append(cur); cur = ''
        else: cur += ch
    if cur.strip(): out.append(cur)
    names = []
    for p in out:
        p = p.split('=')[0].strip()
        if p: names.append(p.split()[-1])
    return names

pat = re.compile(r'\[HarmonyPatch\(typeof\((\w+)\),\s*nameof\(\1\.(\w+)\)(?:,\s*[^\]]*)?\)\]')
bad = 0
for dp, _, fn in os.walk(src):
    for f in fn:
        if not f.endswith('.cs'): continue
        text = open(os.path.join(dp, f), encoding='utf-8').read()
        for m in pat.finditer(text):
            cls, name = m.group(1), m.group(2)
            line = text[:m.start()].count('\n') + 1
            full = text[m.start():m.end()]
            has_args = full.count('typeof') > 1 or 'new[]' in full or 'Type[]' in full
            ms = methods(cls, name)
            tag = f'{f}:{line} {cls}.{name}'
            if ms is None: print('?? ', tag, '(stub file not found)'); continue
            if not ms: print('MISSING  ', tag); bad += 1; continue
            if len(ms) > 1 and not has_args: print('AMBIGUOUS', tag, [x[1][:100] for x in ms]); bad += 1; continue
            # validate patch-method parameter names against the original
            end = text.find('[HarmonyPatch', m.end())
            body = text[m.end(): end if end > 0 else len(text)]
            orig = params_of(ms[0][1]) if len(ms) == 1 else None
            probs = []
            for pm in re.finditer(r'static\s+\w+\s+(Prefix|Postfix|Finalizer)\s*\(([^)]*)\)', body):
                for prm in [x.strip() for x in pm.group(2).split(',') if x.strip()]:
                    nm = prm.split()[-1]
                    if nm.startswith('__') or nm.startswith('___'): continue
                    if orig is not None and nm not in orig: probs.append(f'{pm.group(1)} param "{nm}" not in {orig}')
            if probs: print('PARAMNAME', tag, probs); bad += 1; continue
            print(f'ok cc={ms[0][0]:>2} ', tag)
print('\nproblems:', bad)
sys.exit(1 if bad else 0)
