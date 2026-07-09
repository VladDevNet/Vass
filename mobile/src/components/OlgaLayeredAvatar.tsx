import { useEffect, useRef } from 'react';
import { Animated, Image, StyleSheet, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { haloByState } from '../theme/amoled';

const AVATAR_SIZE = 220;

interface OlgaLayeredAvatarProps {
  state: VoiceState;
  sleeping: boolean;
  disabled?: boolean;
  onLoadError?: () => void;
}

export function OlgaLayeredAvatar({ state, sleeping, disabled, onLoadError }: OlgaLayeredAvatarProps) {
  const mouthSmall = useRef(new Animated.Value(0)).current;
  const mouthBig = useRef(new Animated.Value(0)).current;
  const blink = useRef(new Animated.Value(0)).current; // 0 = открыты, 1 = закрыты

  // Цикл рта во время speaking — то же чередование двух кадров, что
  // AvatarFace.tsx уже использует для своего нарисованного рта, только
  // кросс-фейдом между двумя overlay-картинками вместо scaleY фигуры.
  useEffect(() => {
    if (state === 'speaking') {
      const loop = Animated.loop(
        Animated.sequence([
          Animated.timing(mouthSmall, { toValue: 1, duration: 130, useNativeDriver: true }),
          Animated.timing(mouthSmall, { toValue: 0, duration: 0, useNativeDriver: true }),
          Animated.timing(mouthBig, { toValue: 1, duration: 130, useNativeDriver: true }),
          Animated.timing(mouthBig, { toValue: 0, duration: 0, useNativeDriver: true }),
        ])
      );
      loop.start();
      return () => loop.stop();
    }
    mouthSmall.setValue(0);
    mouthBig.setValue(0);
  }, [state, mouthSmall, mouthBig]);

  // Моргание — непрерывно вне зависимости от state, как в AvatarFace.tsx,
  // КРОМЕ sleeping, где closed-eyes overlay держится полностью открытым
  // (opacity: 1) статично, без моргания поверх него.
  useEffect(() => {
    if (sleeping) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const scheduleBlink = () => {
      const delay = 2200 + Math.random() * 2600;
      timer = setTimeout(() => {
        if (stopped) return;
        Animated.sequence([
          Animated.timing(blink, { toValue: 1, duration: 90, useNativeDriver: true }),
          Animated.timing(blink, { toValue: 0, duration: 130, useNativeDriver: true }),
        ]).start(() => scheduleBlink());
      }, delay);
    };
    scheduleBlink();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [sleeping, blink]);

  const halo = haloByState[state];

  return (
    <View style={[styles.wrapper, disabled && styles.disabled]}>
      {!sleeping && <HaloGlow color={halo.color} intensity={halo.intensity} size={AVATAR_SIZE} />}
      <Image
        source={require('../../assets/avatar/olga_base.png')}
        style={[
          styles.portrait,
          sleeping && styles.sleepingPortrait,
          state === 'paused' && !sleeping && styles.pausedPortrait,
        ]}
        onError={onLoadError}
      />
      {sleeping ? (
        <Image source={require('../../assets/avatar/olga_eyes_closed_overlay.png')} style={styles.portrait} />
      ) : (
        <Animated.Image
          source={require('../../assets/avatar/olga_eyes_closed_overlay.png')}
          style={[styles.portrait, { opacity: blink }]}
        />
      )}
      {state === 'speaking' && (
        <>
          <Animated.Image
            source={require('../../assets/avatar/olga_mouth_open_small_overlay.png')}
            style={[styles.portrait, { opacity: mouthSmall }]}
          />
          <Animated.Image
            source={require('../../assets/avatar/olga_mouth_open_big_overlay.png')}
            style={[styles.portrait, { opacity: mouthBig }]}
          />
        </>
      )}
      {state === 'thinking' && (
        <Image source={require('../../assets/avatar/olga_brows_thinking_overlay.png')} style={styles.portrait} />
      )}
    </View>
  );
}

// Halo как стопка полупрозрачных кругов за портретом — приближение
// radial-gradient без библиотек градиентов (expo-linear-gradient/
// react-native-svg сознательно не добавлены, см. spec). `color` — строка
// вида "R,G,B" без обёртки rgb(...), собирается в rgba() здесь.
function HaloGlow({ color, intensity, size }: { color: string; intensity: number; size: number }) {
  const rings = [
    { extra: 90, alpha: intensity * 0.15 },
    { extra: 55, alpha: intensity * 0.3 },
    { extra: 24, alpha: intensity * 0.55 },
  ];
  return (
    <View style={styles.haloContainer} pointerEvents="none">
      {rings.map((ring) => {
        const d = size + ring.extra;
        return (
          <View
            key={ring.extra}
            style={[
              styles.haloRing,
              {
                width: d,
                height: d,
                borderRadius: d / 2,
                backgroundColor: `rgba(${color},${ring.alpha})`,
              },
            ]}
          />
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    width: AVATAR_SIZE,
    height: AVATAR_SIZE,
    alignItems: 'center',
    justifyContent: 'center',
  },
  disabled: {
    opacity: 0.6,
  },
  portrait: {
    position: 'absolute',
    width: AVATAR_SIZE,
    height: AVATAR_SIZE,
    borderRadius: AVATAR_SIZE / 2,
  },
  // Затемнение через opacity поверх ЧЁРНОГО фона — визуально близко к
  // brightness()-фильтру из веб-мокапа, но не тот же механизм (RN Image
  // не имеет built-in CSS filter). Разные значения — sleeping заметно
  // темнее paused, см. spec.
  sleepingPortrait: {
    opacity: 0.5,
  },
  pausedPortrait: {
    opacity: 0.8,
  },
  haloContainer: {
    position: 'absolute',
    alignItems: 'center',
    justifyContent: 'center',
  },
  haloRing: {
    position: 'absolute',
  },
});
