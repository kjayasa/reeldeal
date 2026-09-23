#!/usr/bin/env bash
# Resets cms.db so it contains exactly one admin with a known password.
# Run before committing cms.db. Prompts for the password (hidden, twice) and pipes it to
# `dotnet run -- --reset-admin`, so it never appears on the command line or in history.
#
#   ./tools/set-admin.sh you@example.com
set -euo pipefail

email="${1:-}"
if [[ -z "$email" ]]; then
  echo "Usage: $0 <email>" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

read -r -s -p "Password for $email (min 8 chars): " password; echo
if (( ${#password} < 8 )); then echo "Password must be at least 8 characters." >&2; exit 1; fi
read -r -s -p "Confirm password: " confirm; echo
if [[ "$password" != "$confirm" ]]; then echo "Passwords don't match." >&2; exit 1; fi

echo "Resetting admins in $repo_root/cms.db ..."
cd "$repo_root"
printf '%s\n' "$password" | dotnet run -- --reset-admin "$email"

echo
echo "Done. cms.db now has a single admin ($email). You can commit it."
echo "Remember: deploying this cms.db over a live one resets production admins too (see README)."
