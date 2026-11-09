#!/bin/sh
set -eu

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    echo "Usage: sh scripts/release-notes.sh VERSION [CHANGELOG]" >&2
    exit 2
fi

version=$1
changelog=${2:-CHANGELOG.md}

awk -v heading="## $version" '
    { sub(/\r$/, "") }
    /^## / {
        if (found) exit
        if ($0 == heading) found = 1
    }
    found { print }
    END {
        if (!found) {
            print "Release notes section not found: " heading > "/dev/stderr"
            exit 1
        }
    }
' "$changelog"
