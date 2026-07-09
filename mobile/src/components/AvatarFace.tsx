import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';

// Plain 2D face — not the Rive vector-avatar docs/react-native/tts-and-avatar.md
// originally proposed for Phase 3. Rive needs its visual editor to author the
// .riv state machine/blink art, which isn't available in this environment;
// built with plain React Native View + Animated instead, so it's fully
// code-driven and (unlike the voice loop) actually checkable via web preview
// screenshots. Revisit Rive later if a hand-authored asset becomes available.
interface Props {
  state: VoiceState;
}

const HEAD_COLOR: Record<VoiceState, string> = {
  idle: '#FFE1C4',
  recording: '#FFD3D3',
  thinking: '#FFE1C4',
  speaking: '#D9F2DD',
  paused: '#E0E0E4',
};

export function AvatarFace({ state }: Props) {
  const blink = useRef(new Animated.Value(1)).current; // 1 = open, 0 = closed
  const mouthOpen = useRef(new Animated.Value(0)).current; // 0 = closed .. 1 = open
  const dot1 = useRef(new Animated.Value(0.3)).current;
  const dot2 = useRef(new Animated.Value(0.3)).current;
  const dot3 = useRef(new Animated.Value(0.3)).current;

  // Blinking runs continuously regardless of voice state — a still face
  // reads as "frozen", not "calm".
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;

    const scheduleBlink = () => {
      const delay = 2200 + Math.random() * 2600;
      timer = setTimeout(() => {
        if (stopped) return;
        Animated.sequence([
          Animated.timing(blink, { toValue: 0, duration: 90, useNativeDriver: true, easing: Easing.in(Easing.quad) }),
          Animated.timing(blink, { toValue: 1, duration: 130, useNativeDriver: true, easing: Easing.out(Easing.quad) }),
        ]).start(() => scheduleBlink());
      }, delay);
    };
    scheduleBlink();

    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [blink]);

  // Mouth: talks while speaking, otherwise settles to a neutral closed shape.
  useEffect(() => {
    if (state === 'speaking') {
      const loop = Animated.loop(
        Animated.sequence([
          Animated.timing(mouthOpen, { toValue: 1, duration: 130, useNativeDriver: true }),
          Animated.timing(mouthOpen, { toValue: 0.25, duration: 130, useNativeDriver: true }),
        ])
      );
      loop.start();
      return () => loop.stop();
    }
    Animated.timing(mouthOpen, { toValue: 0, duration: 150, useNativeDriver: true }).start();
  }, [state, mouthOpen]);

  // Thinking dots: a small sequential pulse, the universal "..." pattern.
  useEffect(() => {
    if (state !== 'thinking') {
      dot1.setValue(0.3);
      dot2.setValue(0.3);
      dot3.setValue(0.3);
      return;
    }
    const pulse = (value: Animated.Value, delay: number) =>
      Animated.loop(
        Animated.sequence([
          Animated.delay(delay),
          Animated.timing(value, { toValue: 1, duration: 300, useNativeDriver: true }),
          Animated.timing(value, { toValue: 0.3, duration: 300, useNativeDriver: true }),
        ])
      );
    const anim = Animated.parallel([pulse(dot1, 0), pulse(dot2, 150), pulse(dot3, 300)]);
    anim.start();
    return () => anim.stop();
  }, [state, dot1, dot2, dot3]);

  return (
    <View style={styles.wrapper}>
      <View style={[styles.head, { backgroundColor: HEAD_COLOR[state] }]}>
        <View style={styles.eyesRow}>
          <Animated.View style={[styles.eye, { transform: [{ scaleY: blink }] }]} />
          <Animated.View style={[styles.eye, { transform: [{ scaleY: blink }] }]} />
        </View>
        <Animated.View
          style={[
            styles.mouth,
            {
              transform: [
                {
                  scaleY: mouthOpen.interpolate({ inputRange: [0, 1], outputRange: [1, 3.4] }),
                },
              ],
            },
          ]}
        />
      </View>
      <View style={styles.dotsRow}>
        {state === 'thinking' && (
          <>
            <Animated.View style={[styles.dot, { opacity: dot1 }]} />
            <Animated.View style={[styles.dot, { opacity: dot2 }]} />
            <Animated.View style={[styles.dot, { opacity: dot3 }]} />
          </>
        )}
      </View>
    </View>
  );
}

const HEAD_SIZE = 200;

const styles = StyleSheet.create({
  wrapper: {
    alignItems: 'center',
    marginBottom: 12,
  },
  head: {
    width: HEAD_SIZE,
    height: HEAD_SIZE,
    borderRadius: HEAD_SIZE / 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  eyesRow: {
    flexDirection: 'row',
    gap: 34,
    marginBottom: 22,
  },
  eye: {
    width: 20,
    height: 20,
    borderRadius: 10,
    backgroundColor: '#3a3a3a',
  },
  mouth: {
    width: 46,
    height: 10,
    borderRadius: 6,
    backgroundColor: '#c0684f',
  },
  dotsRow: {
    flexDirection: 'row',
    gap: 6,
    height: 20,
    marginTop: 12,
    alignItems: 'center',
  },
  dot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: '#999',
  },
});
