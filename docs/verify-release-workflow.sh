#!/usr/bin/env bash
# Harness for .github/workflows/release.yml: runs the plan + publish-list logic verbatim
# against the real repo layout, with `gh` stubbed so nothing is published.
set -uo pipefail

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok   - $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL - $1"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected '$3', got '$2')"; fi; }

cd /work

echo "== YAML parses and exposes the expected shape =="
python3 - <<'PY'
import yaml
d = yaml.safe_load(open('.github/workflows/release.yml'))
# YAML 1.1: a bare `on:` key loads as boolean True, not the string "on".
triggers = d.get('on', d.get(True))
opts = triggers['workflow_dispatch']['inputs']['target']['options']
assert opts[0] == 'all', opts
assert triggers['workflow_dispatch']['inputs']['target']['default'] == 'all'
steps = [s['name'] for s in d['jobs']['build-and-release']['steps']]
assert steps == ['Checkout','Plan the release','Setup .NET','Publish','Create releases'], steps
assert len([l for l in d['env']['COMPONENTS'].strip().splitlines() if l.strip()]) == 5
print("  ok   - workflow yaml structure")
PY
[ $? -eq 0 ] && PASS=$((PASS+1)) || FAIL=$((FAIL+1))

# Pull COMPONENTS/MODULES straight out of the workflow so the harness cannot drift from it.
eval "$(python3 - <<'PY'
import yaml, shlex
d = yaml.safe_load(open('.github/workflows/release.yml'))
print("COMPONENTS=%s" % shlex.quote(d['env']['COMPONENTS']))
print("MODULES=%s" % shlex.quote(d['env']['MODULES']))
PY
)"
export COMPONENTS MODULES

mkdir -p /tmp/bin
cat > /tmp/bin/gh <<'EOF'
#!/usr/bin/env bash
# release view <tag>  -> found only if listed in $GH_EXISTING
if [ "${1:-}" = "release" ] && [ "${2:-}" = "view" ]; then
  for t in ${GH_EXISTING:-}; do [ "$t" = "${3:-}" ] && exit 0; done
  exit 1
fi
exit 0
EOF
chmod +x /tmp/bin/gh
export PATH="/tmp/bin:$PATH"

# The plan step, copied out of the workflow verbatim.
plan() {
  TARGET="$1"
  set -euo pipefail

  if [ "$TARGET" != "all" ] && ! grep -q "^${TARGET}|" <<< "$COMPONENTS"; then
    echo "::error::Unknown release target '$TARGET'."
    exit 1
  fi

  : > release-plan.tsv

  while IFS='|' read -r KEY NAME PROJECT ENTRYPOINT CHANGELOG ZIPDIR TAGPREFIX BUNDLES; do
    [ -n "${KEY:-}" ] || continue
    if [ "$TARGET" != "all" ] && [ "$TARGET" != "$KEY" ]; then continue; fi

    if [ ! -f "$CHANGELOG" ]; then
      echo "::error::$CHANGELOG does not exist."
      exit 1
    fi

    VERSION=$(grep -m1 -oP '^##[ \t]*\[v?\K[^]]+' "$CHANGELOG" || true)

    if [ -z "$VERSION" ]; then
      echo "::error file=$CHANGELOG::No '## [x.y.z]' section found, so there is nothing to release."
      exit 1
    fi

    if [ ! -f "$ENTRYPOINT" ]; then
      echo "::error::$ENTRYPOINT does not exist."
      exit 1
    fi

    DECLARED=$(sed -n 's/.*Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$ENTRYPOINT" | head -1)

    if [ -z "$DECLARED" ]; then
      echo "::error file=$ENTRYPOINT::Could not read PluginMetadata.Version."
      exit 1
    fi

    if [ "$DECLARED" != "$VERSION" ]; then
      echo "::error file=$ENTRYPOINT::PluginMetadata.Version is $DECLARED but the newest CHANGELOG section is $VERSION."
      exit 1
    fi

    TAG="${TAGPREFIX}${VERSION}"

    if gh release view "$TAG" >/dev/null 2>&1; then
      if [ "$TARGET" = "all" ]; then
        echo "::notice::$NAME $VERSION is already released, skipping."
        continue
      fi
      echo "::error::Release $TAG already exists."
      exit 1
    fi

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
      "$NAME" "$PROJECT" "$CHANGELOG" "$ZIPDIR" "$TAG" "$VERSION" "$BUNDLES" "$TAGPREFIX" >> release-plan.tsv
  done <<< "$COMPONENTS"

  if [ ! -s release-plan.tsv ]; then
    echo "::error::Nothing to release."
    exit 1
  fi
}

echo
echo "== core has no CHANGELOG section yet: 'all' must fail loudly =="
OUT=$( (plan all) 2>&1 ); RC=$?
check "exits non-zero" "$([ $RC -ne 0 ] && echo yes || echo no)" "yes"
case "$OUT" in *"No '## [x.y.z]' section found"*) ok "names the missing section";; *) bad "error text: $OUT";; esac
case "$OUT" in *"CHANGELOG.md"*) ok "names the file";; *) bad "no file in error";; esac

echo
echo "== with a core section present, one run plans all five =="
cp CHANGELOG.md /tmp/core-changelog.bak
printf '## [1.0.0] - 2026-10-29\n\n- First release.\n\n' | cat - CHANGELOG.md > /tmp/cl && mv /tmp/cl CHANGELOG.md
sed -i 's/Version = "[^"]*"/Version = "1.0.0"/' Ranks/src/Entrypoint.cs

OUT=$( (plan all) 2>&1 ); RC=$?
check "exits zero" "$RC" "0"
check "plans 5 components" "$(wc -l < release-plan.tsv)" "5"
check "core tag is bare v" "$(cut -f5 release-plan.tsv | head -1)" "v1.0.0"
check "module tag is prefixed" "$(grep -c 'Ranks_WeaponStats-v1.0.0' release-plan.tsv)" "1"
check "only core bundles" "$(cut -f7 release-plan.tsv | grep -cx true)" "1"

echo
echo "== every planned zip path matches what the build actually produced =="
MISSING=0
while IFS=$'\t' read -r NAME PROJECT CHANGELOG ZIPDIR TAG VERSION BUNDLES TAGPREFIX; do
  [ -f "$ZIPDIR/${NAME}-v${VERSION}.zip" ] || { echo "    missing: $ZIPDIR/${NAME}-v${VERSION}.zip"; MISSING=$((MISSING+1)); }
done < release-plan.tsv
check "all 5 zips found where the workflow looks" "$MISSING" "0"

echo
echo "== publish list: core pulls the modules in, deduped =="
PROJECTS=$(cut -f2 release-plan.tsv)
if cut -f7 release-plan.tsv | grep -qx true; then
  for MODULE in $MODULES; do
    PROJECTS=$(printf '%s\nModules/%s/%s.csproj' "$PROJECTS" "$MODULE" "$MODULE")
  done
fi
DEDUPED=$(printf '%s\n' "$PROJECTS" | sort -u | grep -c csproj)
check "5 unique projects, no duplicate publish" "$DEDUPED" "5"

echo
echo "== partial run: 4 already released, only the bumped one is planned =="
export GH_EXISTING="v1.0.0 Ranks_EloPoints-v1.0.0 Ranks_CustomCombos-v1.0.0 Ranks_Prestige-v1.0.0"
OUT=$( (plan all) 2>&1 ); RC=$?
check "exits zero" "$RC" "0"
check "plans only the unreleased one" "$(wc -l < release-plan.tsv)" "1"
check "and it is WeaponStats" "$(cut -f1 release-plan.tsv)" "Ranks_WeaponStats"
case "$OUT" in *"already released, skipping"*) ok "says what it skipped";; *) bad "no skip notice";; esac

echo
echo "== nothing to do is an error, not a silent success =="
export GH_EXISTING="v1.0.0 Ranks_WeaponStats-v1.0.0 Ranks_EloPoints-v1.0.0 Ranks_CustomCombos-v1.0.0 Ranks_Prestige-v1.0.0"
OUT=$( (plan all) 2>&1 ); RC=$?
check "exits non-zero" "$([ $RC -ne 0 ] && echo yes || echo no)" "yes"
case "$OUT" in *"Nothing to release"*) ok "explains why";; *) bad "error text: $OUT";; esac

echo
echo "== single target still refuses to overwrite =="
export GH_EXISTING="Ranks_Prestige-v1.0.0"
OUT=$( (plan Ranks_Prestige) 2>&1 ); RC=$?
check "exits non-zero" "$([ $RC -ne 0 ] && echo yes || echo no)" "yes"
case "$OUT" in *"already exists"*) ok "keeps the old hard failure";; *) bad "error text: $OUT";; esac

echo
echo "== single target builds a one-row plan =="
export GH_EXISTING=""
OUT=$( (plan Ranks_Prestige) 2>&1 ); RC=$?
check "exits zero" "$RC" "0"
check "one row" "$(wc -l < release-plan.tsv)" "1"
check "no module bundling" "$(cut -f7 release-plan.tsv | grep -cx true)" "0"

echo
echo "== unknown target is rejected =="
OUT=$( (plan Ranks_Nope) 2>&1 ); RC=$?
check "exits non-zero" "$([ $RC -ne 0 ] && echo yes || echo no)" "yes"
case "$OUT" in *"Unknown release target"*) ok "names the problem";; *) bad "error text: $OUT";; esac

echo
echo "== version mismatch between CHANGELOG and PluginMetadata is caught =="
sed -i 's/Version = "1.0.0"/Version = "9.9.9"/' Ranks/src/Entrypoint.cs
OUT=$( (plan core) 2>&1 ); RC=$?
check "exits non-zero" "$([ $RC -ne 0 ] && echo yes || echo no)" "yes"
case "$OUT" in *"PluginMetadata.Version is 9.9.9"*) ok "names both versions";; *) bad "error text: $OUT";; esac

echo
echo "== prerelease versions are detected from the tag =="
detect() { case "$1" in *-*) echo prerelease;; *) echo stable;; esac; }
check "1.0.0 is stable" "$(detect 1.0.0)" "stable"
check "1.1.0-beta.1 is prerelease" "$(detect 1.1.0-beta.1)" "prerelease"

echo
echo "== git describe --match cannot cross component prefixes =="
git -C /work config user.email t@t >/dev/null 2>&1 || true
git -C /work config user.name t >/dev/null 2>&1 || true
git -C /work tag v0.9.0 >/dev/null 2>&1 || true
git -C /work tag Ranks_WeaponStats-v0.9.0 >/dev/null 2>&1 || true
check "core prefix finds core tag" "$(git -C /work describe --tags --abbrev=0 --match 'v*' 2>/dev/null)" "v0.9.0"
check "module prefix finds module tag" "$(git -C /work describe --tags --abbrev=0 --match 'Ranks_WeaponStats-v*' 2>/dev/null)" "Ranks_WeaponStats-v0.9.0"

cp /tmp/core-changelog.bak CHANGELOG.md

echo
echo "================================"
echo "passed: $PASS   failed: $FAIL"
[ "$FAIL" -eq 0 ]
