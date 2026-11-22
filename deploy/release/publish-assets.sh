#!/usr/bin/env bash
# Called by the guarded publication job with the workflow's contents token.
set -euo pipefail
umask 077

tag=${1:?Specify the deployment tag}
assets=$(realpath -- "${2:?Specify the verified assets directory}")
repo=monstertacle/monstertacle.hosting
sha=${GITHUB_SHA:?}
[[ "${GITHUB_EVENT_NAME:-}" == workflow_dispatch && "${GITHUB_REPOSITORY:-}" == "$repo" &&
    "${GITHUB_REF:-}" == refs/heads/main && "$sha" =~ ^[0-9a-f]{40}$ ]]
[[ "$tag" =~ ^deploy-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ && "${#tag}" -le 64 ]]
name="monstertacle-hosting-$tag-linux-amd64"
bundle="$name-bundle.tar.gz"
images="$name-images.tar.gz"
title="Monstertacle Hosting deployment $tag"
notes=$(printf 'Linux amd64 deployment bundle and checksum-verifiable container images.\n\nSource revision: %s\n\n[Deployment guide](https://github.com/%s/blob/%s/deploy/release/README.md)' "$sha" "$repo" "$sha")
files=(SHA256SUMS "$bundle" "$images")
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT

(cd -- "$assets" && sha256sum --strict --check SHA256SUMS)
[[ "$(tar -xOzf "$assets/$bundle" "$name/revision")" == "$sha" ]]

require_current_main() {
    [[ "$(gh api "repos/$repo/git/ref/heads/main" --jq '.object.sha')" == "$sha" ]]
}

require_bound_tag() {
    [[ "$(gh api "repos/$repo/git/ref/tags/$tag" --jq '.object | select(.type=="commit") | .sha')" == "$sha" ]]
}

require_current_main
# Distinguish an absent tag from a transport or server failure.
if git ls-remote --exit-code --refs "https://github.com/$repo.git" "refs/tags/$tag" > /dev/null; then
    require_bound_tag
else
    status=$?
    [[ "$status" == 2 ]]
    gh api "repos/$repo/git/refs" -f ref="refs/tags/$tag" -f sha="$sha" --silent
    require_bound_tag
fi

release=(gh release)
release_fields=tagName,isDraft,name,body
if ! "${release[@]}" view "$tag" --repo "$repo" --json "$release_fields" > "$scratch/release.json"; then
    "${release[@]}" create "$tag" --repo "$repo" --verify-tag --draft --latest=false \
        --target "$sha" --title "$title" --notes "$notes"
    "${release[@]}" view "$tag" --repo "$repo" --json "$release_fields" > "$scratch/release.json"
fi
jq -e --arg tag "$tag" --arg title "$title" --arg notes "$notes" '
    .tagName == $tag and .name == $title and (.body | rtrimstr("\n")) == $notes
' "$scratch/release.json" >/dev/null

draft=$(jq -r '.isDraft' "$scratch/release.json")
if [[ "$draft" == true ]]; then
    # An interrupted upload can be repaired while the release is still a draft.
    "${release[@]}" upload "$tag" --repo "$repo" --clobber \
        "$assets/SHA256SUMS" "$assets/$bundle" "$assets/$images"
fi

"${release[@]}" download "$tag" --repo "$repo" --dir "$scratch" \
    --pattern SHA256SUMS --pattern "$bundle" --pattern "$images"
for file in "${files[@]}"; do
    cmp -- "$assets/$file" "$scratch/$file"
done
if [[ "$draft" == true ]]; then
    require_current_main
    require_bound_tag
    "${release[@]}" edit "$tag" --repo "$repo" --draft=false --latest=false
else
    printf 'The deployment release already contains the verified assets.\n'
fi
