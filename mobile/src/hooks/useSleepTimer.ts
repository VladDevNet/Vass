import { useEffect, useState } from 'react';

// Чисто визуальный, презентационный таймер — НЕ часть VoiceState, не
// трогает useVoiceChat.ts. См. spec: VoiceState глубоко завязан на живую
// логику VAD/микрофона, и добавление туда нового значения потребовало бы
// такого же цикла ревью, как pause/resume (4 раунда, см. PR #59).
//
// `active`, а не сырой VoiceState — сознательно: единственное, что имеет
// значение для сна, это "сейчас idle или нет", а не конкретный ЛИБО
// предыдущий стейт. HomeScreen передаёт `state === 'idle'`.
//
// Любое изменение `active` (не только true→false, но и false→false у
// другого рендера — React всё равно не перезапустит эффект без реальной
// смены значения) сбрасывает sleeping и таймер. Пока active остаётся
// false (recording/thinking/speaking/paused), эффект не перезапускается
// вообще — и не должен: спать можно только из during idle.
export function useSleepTimer(active: boolean, sleepAfterMs: number): boolean {
  const [sleeping, setSleeping] = useState(false);

  useEffect(() => {
    setSleeping(false);
    if (!active) return;
    const timer = setTimeout(() => setSleeping(true), sleepAfterMs);
    return () => clearTimeout(timer);
  }, [active, sleepAfterMs]);

  return sleeping;
}
