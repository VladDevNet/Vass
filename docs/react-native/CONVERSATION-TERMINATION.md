# Explicit conversation termination

The power icon in the Vass home header opens a confirmation dialog. Confirming it is intentionally stronger than a pause:

1. invalidates the current voice-turn generations and aborts every active audio upload, continuation transcription, and SSE response;
2. stops TTS, both recorders, VAD, background recording, and the Android overlay;
3. removes the Android task with `finishAndRemoveTask()` and also stops the one-shot MediaProjection service if a screen capture is in progress;
4. preserves server history, long-term memory, and scheduled local reminders.

The runtime cannot auto-resume from a late VAD tick, stale network response, overlay event, or background-to-foreground transition after termination. If Android keeps the process alive after task removal, the next explicit launcher open starts a fresh listening cycle; it does not resume the abandoned turn.

## Physical-device acceptance

Run each scenario while the overlay is both enabled and disabled:

- End while Vass is listening, then confirm no microphone/foreground-service notification remains.
- End while a streamed reply is speaking, then confirm audio stops immediately and no remaining sentence starts.
- End while the server is thinking or an audio upload is in flight, then confirm no late text, TTS, YouTube action, or screen-capture retry appears.
- End during Android screen-capture consent, then confirm the task and capture service disappear.
- Reopen Vass from the launcher and confirm a new listening cycle starts while existing history and reminders remain intact.
