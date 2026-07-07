import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, sendMessage } from '../api/client';
import { speakToCompletion, stopSpeaking } from '../tts/systemSpeech';

export type VoiceState = 'idle' | 'recording' | 'thinking' | 'speaking';

// First mobile voice-loop increment (docs/react-native/BACKLOG.md Phase 1):
// tap to record, tap to stop — not the continuous VAD/turn-taking design
// yolo.js implements on the web client yet. That's layered on top of this
// once it's confirmed working on a real device (see BUILD-WSL.md — I can
// build and type-check this, but not exercise a live microphone myself).
export function useVoiceChat(sessionId: number | null) {
  const [state, setState] = useState<VoiceState>('idle');
  const [transcript, setTranscript] = useState('');
  const [reply, setReply] = useState('');
  const [error, setError] = useState<string | null>(null);
  const recorder = useAudioRecorder(RecordingPresets.HIGH_QUALITY);
  const playerRef = useRef<ReturnType<typeof createAudioPlayer> | null>(null);

  useEffect(() => {
    return () => {
      playerRef.current?.release();
      stopSpeaking();
    };
  }, []);

  const startRecording = useCallback(async () => {
    if (state !== 'idle') return; // guards a double-tap racing the idle->recording transition
    setError(null);
    try {
      const permission = await requestRecordingPermissionsAsync();
      if (!permission.granted) {
        setError('Нет доступа к микрофону');
        return;
      }
      await setAudioModeAsync({ playsInSilentMode: true, allowsRecording: true, interruptionMode: 'doNotMix' });
      await recorder.prepareToRecordAsync();
      recorder.record();
      setState('recording');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [recorder, state]);

  const stopAndRespond = useCallback(async () => {
    if (!sessionId) {
      setError('Сессия ещё не готова');
      return;
    }
    setState('thinking');
    setTranscript('');
    setReply('');
    try {
      await recorder.stop();
      const uri = recorder.uri;
      if (!uri) throw new Error('Запись не найдена');

      const { fileName } = await api.uploadAudio(uri);
      const fullReply = await sendMessage(
        { sessionId, message: '', audioFileName: fileName },
        {
          onTranscription: setTranscript,
          onChunk: (chunk) => setReply((prev) => prev + chunk),
        }
      );

      if (fullReply.trim()) {
        setState('speaking');
        // System TTS (expo-speech) is the primary path — instant, no network
        // hop. Its failure modes are all recoverable in JS (missing Russian
        // voice, native "text too long" rejection despite our own chunking,
        // a transient engine error) so any of them falls back to the
        // existing buffered server-Piper path rather than the reply going
        // silent.
        try {
          await speakToCompletion(fullReply);
        } catch {
          const audioUri = await api.synthesizeSpeech(fullReply);
          await playToCompletion(audioUri, playerRef);
        }
      }

      setState('idle');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setState('idle');
    }
  }, [recorder, sessionId]);

  return { state, transcript, reply, error, startRecording, stopAndRespond };
}

// Generous cap on how long a synthesized reply could plausibly run — a
// safety net so a broken TTS file (bad decode, no didJustFinish event)
// can't leave the UI stuck in 'speaking' forever with the mic disabled.
const MAX_PLAYBACK_MS = 60_000;

function playToCompletion(
  uri: string,
  playerRef: React.MutableRefObject<ReturnType<typeof createAudioPlayer> | null>
): Promise<void> {
  return new Promise((resolve) => {
    const player = createAudioPlayer(uri);
    playerRef.current = player;

    const finish = () => {
      clearTimeout(timeoutId);
      subscription.remove();
      player.release();
      if (playerRef.current === player) playerRef.current = null;
      resolve();
    };

    const timeoutId = setTimeout(finish, MAX_PLAYBACK_MS);
    const subscription = player.addListener('playbackStatusUpdate', (status) => {
      if (status.didJustFinish || status.error) finish();
    });
    player.play();
  });
}
