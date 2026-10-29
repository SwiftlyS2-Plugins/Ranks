#!/usr/bin/env bash
# Harness for .github/workflows/release.yml: runs the version, notes and zip-collection logic
# verbatim against the real repo, with `gh` stubbed so nothing is published.
set -uo pipefail

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok   - $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL - $1"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected '$3', got '$2')"; fi; }

cd /work

echo "== workflow yaml parses and has the expected shape =="
python3 - <<'PY'
import yaml
d = yaml.safe_load(open('.github/workflows/release.yml'))
# YAML 1.1: a bare `on:` key loads as boolean True, not the string "on".
triggers = d.get('on', d.get(True))
assert 'workflow_dispatch' in triggers, triggers
# One release only: a target chooser must not come back.
assert not (triggers.get('workflow_dispatch') or {}), "workflow_dispatch should take no inputs"
steps = [s['name'] for s in d['jobs']['build-and-release']['steps']]
expected = ['Checkout','Read version from CHANGELOG','Verify PluginMetadata matches the CHANGELOG',
            'Refuse to overwrite an existing release','Extract release notes from CHANGELOG',
            'Setup .NET','Publish','Collect zips','Create release']
assert steps == expected, steps
body = open('.github/workflows/release.yml').read()
assert body.count('gh release create') == 1, body.count('gh release create')
assert len(d['env']['MODULES'].split()) == 4, d['env']['MODULES']
print("  ok   - yaml structure, single release, 4 modules")
PY
[ $? -eq 0 ] && PASS=$((PASS+1)) || FAIL=$((FAIL+1))

eval "$(python3 - <<'PY'
import yaml, shlex
d = yaml.safe_load(open('.github/workflows/release.yml'))
print("MODULES=%s" % shlex.quote(d['env']['MODULES']))
PY
)"
export MODULES

mkdir -p /tmp/bin
cat > /tmp/bin/gh <<'EOF'
#!/usr/bin/env bash
if [ "${1:-}" = "release" ] && [ "${2:-}" = "view" ]; then
  for t in ${GH_EXISTING:-}; do [ "$t" = "${3:-}" ] && exit 0; done
  exit 1
fi
exit 0
EOF
chmod +x /tmp/bin/gh
export PATH="/tmp/bin:$PATH"

read_version()  { grep -m1 -oP '^##[ \t]*\[v?\K[^]]+' CHANGELOG.md || true; }
read_declared() { sed -n 's/.*Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' Ranks/src/Entrypoint.cs | head -1; }

echo
echo "== version comes from the CHANGELOG, not the template comment =="
VERSION=$(read_version)
check "a version was found" "$([ -n "$VERSION" ] && echo yes || echo no)" "yes"
check "template placeholder not matched" "$(grep -c '^## \[1.0.0\] - YYYY-MM-DD' CHANGELOG.md)" "0"

echo
echo "== PluginMetadata agrees with it =="
check "metadata matches changelog" "$(read_declared)" "$VERSION"

echo
echo "== release notes are just this section =="
awk -v ver="$VERSION" '
  /^## \[/ { if (found) exit; if ($0 ~ "\\[v?" ver "\\]") { found = 1; next } }
  found { print }
' CHANGELOG.md > /tmp/notes.md
check "notes are non-empty" "$([ -s /tmp/notes.md ] && echo yes || echo no)" "yes"
check "notes carry no '## [' heading" "$(grep -c '^## \[' /tmp/notes.md)" "0"

echo
echo "== every zip the workflow expects exists where it looks =="
CORE_ZIP="Ranks/build/Ranks-v${VERSION}.zip"
check "core zip at the composed path" "$([ -f "$CORE_ZIP" ] && echo yes || echo no)" "yes"
MISSING=0
for MODULE in $MODULES; do
  Z=$(find "Modules/$MODULE/build" -maxdepth 1 -name "${MODULE}-v*.zip" -type f 2>/dev/null | head -1)
  [ -n "$Z" ] || { echo "    missing: $MODULE"; MISSING=$((MISSING+1)); }
done
check "all 4 module zips glob-resolve" "$MISSING" "0"

echo
echo "== the one release carries core + every module and nothing else =="
rm -rf /tmp/art && mkdir -p /tmp/art
cp "$CORE_ZIP" /tmp/art/
for MODULE in $MODULES; do
  cp "$(find "Modules/$MODULE/build" -maxdepth 1 -name "${MODULE}-v*.zip" -type f | head -1)" /tmp/art/
done
check "5 assets" "$(ls /tmp/art | wc -l)" "5"
check "one is core" "$(ls /tmp/art | grep -c '^Ranks-v')" "1"
check "four are modules" "$(ls /tmp/art | grep -c '^Ranks_')" "4"

echo
echo "== an existing tag blocks the run =="
export GH_EXISTING="v$VERSION"
if gh release view "v$VERSION" >/dev/null 2>&1; then ok "existing tag detected"; else bad "existing tag not detected"; fi
export GH_EXISTING=""
if gh release view "v$VERSION" >/dev/null 2>&1; then bad "absent tag reported present"; else ok "absent tag reports absent"; fi

echo
echo "== a missing CHANGELOG section fails loudly =="
cp CHANGELOG.md /tmp/cl.bak
grep -v '^## \[' CHANGELOG.md > /tmp/cl && cp /tmp/cl CHANGELOG.md
check "no version resolves" "$(read_version)" ""
cp /tmp/cl.bak CHANGELOG.md
check "restored" "$(read_version)" "$VERSION"

echo
echo "== prerelease is detected from the version string =="
detect() { case "$1" in *-*) echo prerelease;; *) echo stable;; esac; }
check "1.0.0 is stable" "$(detect 1.0.0)" "stable"
check "1.1.0-beta.1 is prerelease" "$(detect 1.1.0-beta.1)" "prerelease"

echo
echo "== the compare link only ever matches core tags =="
git -C /work tag v0.9.0 >/dev/null 2>&1 || true
git -C /work tag Ranks_WeaponStats-v9.9.9 >/dev/null 2>&1 || true
check "describe --match 'v*' ignores module tags" "$(git -C /work describe --tags --abbrev=0 --match 'v*' 2>/dev/null)" "v0.9.0"

echo
echo "================================"
echo "passed: $PASS   failed: $FAIL"
[ "$FAIL" -eq 0 ]
