#!/bin/sh
set -e

# /app/audio and /app/visual are named Docker volumes — their on-disk
# ownership persists from whenever each volume was first created, and a
# mount shadows the image's build-time `chown`. Fix both on every start so a
# new visual volume is immediately writable by the non-root app user.
#
# PROJECT-AUDIT-2026-07-10 section 6: a full recursive chown gets slower as
# the volume accumulates files across a long-running deploy history, on
# every single restart. Every file under here is created by this same
# container running AS app (the exec below) -- so once the top-level
# directory itself is owned by app:app, everything beneath it already is
# too, and re-walking the whole tree again is redundant. Checking just that
# one stat skips the expensive recursive chown on every restart after the
# first (or after a genuinely new/mis-owned volume).
for media_dir in /app/audio /app/visual; do
  if [ "$(stat -c '%U' "$media_dir" 2>/dev/null)" != "app" ]; then
    chown -R app:app "$media_dir"
  fi
done

exec setpriv --reuid=app --regid=app --init-groups dotnet VoiceAssistant.API.dll
