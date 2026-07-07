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
chown -R app:app /app/audio

exec setpriv --reuid=app --regid=app --init-groups dotnet VoiceAssistant.API.dll
