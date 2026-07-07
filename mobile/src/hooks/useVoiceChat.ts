import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, sendMessage } from '../api/client';

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
    };
  }, []);

  const startRecording = useCallback(async () => {
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
  }, [recorder]);

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
        const audioUri = await api.synthesizeSpeech(fullReply);
        await playToCompletion(audioUri, playerRef);
      }

      setState('idle');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setState('idle');
    }
  }, [recorder, sessionId]);

  return { state, transcript, reply, error, startRecording, stopAndRespond };
}

function playToCompletion(
  uri: string,
  playerRef: React.MutableRefObject<ReturnType<typeof createAudioPlayer> | null>
): Promise<void> {
  return new Promise((resolve) => {
    const player = createAudioPlayer(uri);
    playerRef.current = player;
    const subscription = player.addListener('playbackStatusUpdate', (status) => {
      if (status.didJustFinish) {
        subscription.remove();
        player.release();
        playerRef.current = null;
        resolve();
      }
    });
    player.play();
  });
}
