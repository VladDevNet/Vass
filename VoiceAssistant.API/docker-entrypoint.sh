#!/bin/sh
set -e

# /app/audio is a named Docker volume — its on-disk ownership persists from
# whenever the volume was first created, and mounting a volume over a
# directory shadows whatever the image's build-time `chown` set up. This bit
# us for real: the volume predated the tutor->app user rename, stayed owned
# by the old uid, and every upload-audio write 500'd with Permission denied
# regardless of anything in application code. Fix it on every start
# (idempotent, cheap) so this can't silently recur for a future fresh volume
# either.
#
# PROJECT-AUDIT-2026-07-10 section 6: a full recursive chown gets slower as
# the volume accumulates files across a long-running deploy history, on
# every single restart. Every file under here is created by this same
# container running AS app (the exec below) -- so once the top-level
# directory itself is owned by app:app, everything beneath it already is
# too, and re-walking the whole tree again is redundant. Checking just that
# one stat skips the expensive recursive chown on every restart after the
# first (or after a genuinely new/mis-owned volume).
if [ "$(stat -c '%U' /app/audio 2>/dev/null)" != "app" ]; then
  chown -R app:app /app/audio
fi

exec setpriv --reuid=app --regid=app --init-groups dotnet VoiceAssistant.API.dll
