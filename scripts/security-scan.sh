#!/usr/bin/env bash
set -euo pipefail

# This guard deliberately reports file names and scopes only. It must never print
# matching lines because those lines may contain the credential being detected.
PATTERN='AKIA[0-9A-Z]{16}|(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|client[_-]?secret["'"']?[[:space:]]*[:=][[:space:]]*["'"'][^"'"']{16,}|(https?|ssh|postgres(ql)?|mysql)://[^[:space:]\/@]+:[^[:space:]\/@]+@[^[:space:]]+'

FAILED=0

report_git_hits() {
    local scope="$1"
    shift
    local hits
    hits="$(git grep --no-index -I -l -E "$PATTERN" "$@" 2>/dev/null || true)"
    if [[ -n "$hits" ]]; then
        echo "secret-scan: candidate detected in $scope (paths withheld from matching content)" >&2
        echo "$hits" | sed 's/^/secret-scan: candidate path: /' >&2
        FAILED=1
    fi
}

report_tree_hits() {
    local scope="$1"
    shift
    local hits
    hits="$(git grep -I -l -E "$PATTERN" "$scope" -- . ':!tests/**' 2>/dev/null || true)"
    if [[ -n "$hits" ]]; then
        echo "secret-scan: candidate detected in Git tree $scope" >&2
        echo "$hits" | sed 's/^/secret-scan: candidate path: /' >&2
        FAILED=1
    fi
}

report_git_hits "working tree" -- . ':!.git/**' ':!tests/**'
report_tree_hits HEAD

while IFS= read -r commit; do
    [[ -z "$commit" ]] && continue
    report_tree_hits "$commit"
done < <(git rev-list --all)

scan_artifact_file() {
    local file="$1"
    if strings -- "$file" 2>/dev/null | grep -E -q "$PATTERN"; then
        echo "secret-scan: candidate detected in artifact scope (path withheld from matching content)" >&2
        echo "secret-scan: candidate path: $file" >&2
        FAILED=1
    fi
}

for artifact in "$@"; do
    [[ -e "$artifact" ]] || continue
    if [[ -f "$artifact" ]]; then
        scan_artifact_file "$artifact"
        continue
    fi

    while IFS= read -r -d '' file; do
        scan_artifact_file "$file"
    done < <(find "$artifact" -type f -print0)
done

if (( FAILED != 0 )); then
    exit 1
fi

echo "secret-scan: no high-confidence credential patterns detected"
