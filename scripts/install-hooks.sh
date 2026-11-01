#!/bin/sh
set -eu
root=$(git rev-parse --show-toplevel)
cd "$root"
if [ ! -x .githooks/pre-push ]; then
  echo 'Tracked .githooks/pre-push is missing or not executable. Restore its executable bit first.' >&2
  exit 1
fi
if current=$(git config --get core.hooksPath); then
  if [ "$current" = '.githooks' ]; then
    echo 'Tracked hooks already selected.'
    exit 0
  fi
  echo 'Refusing to replace existing core.hooksPath. Review existing hooks manually.' >&2
  exit 1
fi
hooks=$(git rev-parse --git-path hooks)
if [ -d "$hooks" ]; then
  active_hooks=$(find -H "$hooks" -mindepth 1 -maxdepth 1 ! -name '*.sample' -print)
  if [ -n "$active_hooks" ]; then
    echo 'Refusing to bypass existing hooks. Review existing hooks manually.' >&2
    exit 1
  fi
fi
git config --local core.hooksPath .githooks
echo 'Selected tracked hooks for this repository. Linked worktrees share this repository-local setting.'
