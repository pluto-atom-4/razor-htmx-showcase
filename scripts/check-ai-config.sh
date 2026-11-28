#!/usr/bin/env bash
# Pre-commit check for staged .claude/ and .mcp.json changes; reads the index, not the worktree.
# git commit --no-verify skips it.
set -uo pipefail

top=$(git rev-parse --show-toplevel) || { echo "check-ai-config: git rev-parse --show-toplevel failed" >&2; exit 2; }
cd "$top" || { echo "check-ai-config: cannot cd to $top" >&2; exit 2; }
changed=$(git diff --cached --name-only --no-renames --diff-filter=d -- .claude .mcp.json) \
  || { echo "check-ai-config: git diff failed" >&2; exit 2; }
[ -n "$changed" ] || exit 0
tmp=$(mktemp) || { echo "check-ai-config: mktemp failed" >&2; exit 2; }
trap 'rm -f "$tmp"' EXIT

report() { printf 'check-ai-config: %s\n' "$*" >&2; failed=1; }

scan_lines() {
  # $1 file, $2 label, $3 grep options, $4 pattern; reports line numbers only
  local f=$1 label=$2 out rc=0 n
  out=$(LC_ALL=C grep "$3" -e "$4" "$tmp") || rc=$?
  case $rc in
    0) ;;
    1) return 0 ;;
    *) report "$f: scan error"; return 0 ;;
  esac
  for n in $(printf '%s\n' "$out" | cut -d: -f1); do
    report "$f:$n: $label"
  done
}

scan_email() {
  local f=$1 out kept rc=0 n
  out=$(LC_ALL=C grep -anoE -e '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' "$tmp") || rc=$?
  case $rc in
    0) ;;
    1) return 0 ;;
    *) report "$f: scan error"; return 0 ;;
  esac
  rc=0
  kept=$(printf '%s\n' "$out" | LC_ALL=C grep -viE -e '^[0-9]+:(git@github\.com|noreply@anthropic\.com|[^@]+@users\.noreply\.github\.com|[^@]+@([A-Za-z0-9-]+\.)*example\.(com|org|net))$') || rc=$?
  case $rc in
    0) ;;
    1) return 0 ;;
    *) report "$f: scan error"; return 0 ;;
  esac
  for n in $(printf '%s\n' "$kept" | cut -d: -f1); do
    report "$f:$n: email address"
  done
}

check_file() {
  local f=$1 err
  if [ "$f" = .claude/settings.local.json ]; then
    # Not read or scanned: it is machine-local and may hold secrets.
    report "$f: machine-local file, unstage it: git restore --staged .claude/settings.local.json"
    return 0
  fi
  if ! git cat-file blob ":0:$f" > "$tmp" 2>/dev/null; then
    report "$f: cannot read staged content"
    return 0
  fi

  case $f in
    .claude/settings.json|.mcp.json)
      if ! command -v jq >/dev/null 2>&1; then
        report "$f: jq not found, cannot validate JSON"
      elif ! err=$(jq -e -s 'length == 1' < "$tmp" 2>&1 >/dev/null); then
        report "$f: invalid JSON: ${err:-must hold exactly one JSON value}"
      fi
      ;;
    .claude/hooks/*.sh)
      if LC_ALL=C grep -qU -e $'\r' "$tmp"; then
        report "$f: CR line ending"
      fi
      if ! err=$("$BASH" -n < "$tmp" 2>&1); then
        report "$f: shell syntax error: $err"
      fi
      ;;
  esac

  scan_lines "$f" "possible literal token" -anE 'gh[pousr]_[A-Za-z0-9]{20,}'
  scan_lines "$f" "possible literal token" -anE 'github_pat_[A-Za-z0-9_]{20,}'
  scan_lines "$f" "possible literal token" -anE '(^|[^A-Za-z0-9_-])sk-[A-Za-z0-9_-]{20,}'
  scan_lines "$f" "possible literal token" -anE '(^|[^A-Za-z0-9])(AKIA|ASIA)[0-9A-Z]{16}'
  scan_lines "$f" "possible literal token" -anE 'xox[abprs]-[A-Za-z0-9-]{10,}'
  scan_lines "$f" "possible literal token" -anE 'AIza[0-9A-Za-z_-]{35}'
  scan_lines "$f" "possible literal token" -anE '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  scan_lines "$f" "possible literal token" -anE 'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
  scan_lines "$f" "possible literal token" -aniE 'bearer[[:space:]]+[A-Za-z0-9._~+/=-]{20,}'
  scan_lines "$f" "possible literal token" -aniE '"[A-Za-z0-9_-]*(token|secret|password|passwd|api[_-]?key)"[[:space:]]*:[[:space:]]*"[^"$<[:space:]]{8,}"'
  scan_lines "$f" "home path" -anE '(^|[^A-Za-z0-9._-])/(home|Users)/[A-Za-z0-9._-]+'
  scan_lines "$f" "home path" -anE '(^|[^A-Za-z0-9._-])/root/'
  scan_lines "$f" "home path" -anE '/mnt/[a-z]/Users/[A-Za-z0-9._-]+'
  scan_lines "$f" "home path" -anE '(^|[^A-Za-z0-9])[A-Za-z]:[\\/]+[Uu]sers[\\/]+[A-Za-z0-9._-]+'
  scan_email "$f"
}

failed=0
# Process substitution, not a pipe: keeps the loop in this shell so failed survives; nothing in the loop may read stdin.
while IFS= read -r -d '' f; do
  check_file "$f"
done < <(git diff --cached --name-only -z --no-renames --diff-filter=d -- .claude .mcp.json)

if [ "$failed" -ne 0 ]; then
  echo "check-ai-config: commit refused. Fix, stage again, retry. For a false positive, git commit --no-verify skips all pre-commit hooks." >&2
  exit 1
fi
exit 0
