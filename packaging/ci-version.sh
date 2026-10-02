#!/usr/bin/env bash
# Works out the build version for CI and writes it to $GITHUB_OUTPUT:
#   version       x.y.z      app/assembly and Linux tarball
#   msix_version  x.y.z.0    MSIX package (the Store requires the last part to be 0)
#   is_release    true|false
#
# Tag push (a release): the version comes from the tag, which must be vX.Y.Z (e.g. v1.0.7).
# Anything else (push to main, pull request, manual run): 0.0.<run number>, never mistakable
# for a release.
#
# Inputs (set by GitHub Actions): GITHUB_REF_TYPE, GITHUB_REF_NAME, GITHUB_RUN_NUMBER, GITHUB_OUTPUT.
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  tag="${GITHUB_REF_NAME:-}"
  if [[ ! "$tag" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
    echo "::error::Release tag '$tag' must look like v1.2.3" >&2
    exit 1
  fi
  for part in "${BASH_REMATCH[@]:1}"; do
    # MSIX version parts are 16-bit.
    if (( 10#$part > 65535 )); then
      echo "::error::Version part $part in '$tag' is too large (max 65535)" >&2
      exit 1
    fi
  done
  version="$((10#${BASH_REMATCH[1]})).$((10#${BASH_REMATCH[2]})).$((10#${BASH_REMATCH[3]}))"
  is_release=true
else
  version="0.0.${GITHUB_RUN_NUMBER:-0}"
  is_release=false
fi

echo "Version: $version (release: $is_release)"
{
  echo "version=$version"
  echo "msix_version=$version.0"
  echo "is_release=$is_release"
} >> "${GITHUB_OUTPUT:-/dev/stdout}"
