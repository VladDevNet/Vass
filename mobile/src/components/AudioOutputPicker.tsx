import { useMemo, useState } from 'react';
import { ActivityIndicator, Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { Bluetooth, Check, ChevronDown, Headphones, Volume2 } from 'lucide-react-native';
import type { VoiceAudioOutput } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface AudioOutputPickerProps {
  outputs: VoiceAudioOutput[];
  selectedOutputId: string;
  onRefresh: () => Promise<unknown>;
  onSelect: (outputId: string) => Promise<void>;
}

function OutputIcon({ kind, size = 20 }: { kind: VoiceAudioOutput['kind']; size?: number }) {
  if (kind === 'bluetooth') return <Bluetooth size={size} color={amoled.textPrimary} />;
  if (kind === 'wired') return <Headphones size={size} color={amoled.textPrimary} />;
  return <Volume2 size={size} color={amoled.textPrimary} />;
}

export function AudioOutputPicker({ outputs, selectedOutputId, onRefresh, onSelect }: AudioOutputPickerProps) {
  const [visible, setVisible] = useState(false);
  const [switching, setSwitching] = useState(false);
  const selected = useMemo(
    () => outputs.find((output) => output.id === selectedOutputId) ?? outputs[0],
    [outputs, selectedOutputId],
  );

  async function open() {
    setVisible(true);
    await onRefresh().catch(() => undefined);
  }

  async function select(outputId: string) {
    if (switching || outputId === selectedOutputId) {
      setVisible(false);
      return;
    }
    setSwitching(true);
    try {
      await onSelect(outputId);
      setVisible(false);
    } finally {
      setSwitching(false);
    }
  }

  const currentLabel = selected?.label ?? 'Динамик телефона';
  return (
    <>
      <Pressable
        style={styles.trigger}
        onPress={() => void open()}
        accessibilityRole="button"
        accessibilityLabel={`Вывод звука: ${currentLabel}. Открыть выбор устройства.`}
      >
        <OutputIcon kind={selected?.kind ?? 'speaker'} size={20} />
        <ChevronDown size={13} color={amoled.textSecondary} strokeWidth={2.5} />
      </Pressable>
      <Modal transparent visible={visible} animationType="fade" onRequestClose={() => !switching && setVisible(false)}>
        <View style={styles.backdrop}>
          {!switching && <Pressable style={StyleSheet.absoluteFill} onPress={() => setVisible(false)} accessibilityLabel="Закрыть выбор вывода звука" />}
          <View style={styles.menu} accessibilityViewIsModal>
            <Text style={styles.title}>Вывод звука</Text>
            {outputs.map((output) => {
              const isSelected = output.id === selectedOutputId;
              return (
                <Pressable
                  key={output.id}
                  style={[styles.option, isSelected && styles.optionSelected]}
                  onPress={() => void select(output.id)}
                  disabled={switching}
                  accessibilityRole="button"
                  accessibilityLabel={output.label}
                  accessibilityState={{ selected: isSelected, disabled: switching }}
                >
                  <View style={styles.optionIcon}>
                    <OutputIcon kind={output.kind} />
                  </View>
                  <Text style={styles.optionText} numberOfLines={1}>{output.label}</Text>
                  {switching && isSelected
                    ? <ActivityIndicator size="small" color="#60A5FA" />
                    : isSelected && <Check size={20} color="#60A5FA" strokeWidth={2.75} />}
                </Pressable>
              );
            })}
          </View>
        </View>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  trigger: {
    width: 44,
    height: 40,
    borderRadius: 20,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 1,
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
  },
  backdrop: {
    flex: 1,
    alignItems: 'flex-end',
    paddingTop: 68,
    paddingRight: 20,
  },
  menu: {
    width: 252,
    padding: 8,
    backgroundColor: '#111827',
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 8,
  },
  title: {
    color: amoled.textSecondary,
    fontSize: 13,
    fontWeight: '700',
    marginHorizontal: 10,
    marginTop: 6,
    marginBottom: 5,
  },
  option: {
    minHeight: 52,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    paddingHorizontal: 8,
    borderRadius: 6,
  },
  optionSelected: {
    backgroundColor: 'rgba(59,130,246,0.14)',
  },
  optionIcon: {
    width: 34,
    height: 34,
    alignItems: 'center',
    justifyContent: 'center',
  },
  optionText: {
    flex: 1,
    color: amoled.textPrimary,
    fontSize: 16,
  },
});
