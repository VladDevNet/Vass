import { useCallback, useEffect, useRef, useState } from 'react';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import { api } from '../api/client';
import type { PendingVisualInput, StageVisualAssetInput, VisualInputStatus, VisualSource } from '../visual/types';

const MIME_BY_EXTENSION: Record<string, string> = {
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  webp: 'image/webp',
};

function resolveMimeType(uri: string, reportedMimeType?: string | null): string | null {
  if (reportedMimeType?.startsWith('image/')) return reportedMimeType;
  const extension = uri.split('?')[0].split('.').pop()?.toLowerCase();
  return extension ? MIME_BY_EXTENSION[extension] ?? null : null;
}

export function useVisualInput() {
  const [pendingVisual, setPendingVisual] = useState<PendingVisualInput | null>(null);
  const [status, setStatus] = useState<VisualInputStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [uploadingUri, setUploadingUri] = useState<string | null>(null);
  const pendingRef = useRef<PendingVisualInput | null>(null);
  const operationRef = useRef(0);
  const mountedRef = useRef(true);

  pendingRef.current = pendingVisual;

  useEffect(() => () => {
    mountedRef.current = false;
    operationRef.current += 1;
  }, []);

  const getPendingVisual = useCallback(() => pendingRef.current, []);

  const consumePendingVisual = useCallback((assetId: string) => {
    if (pendingRef.current?.assetId !== assetId) return;
    pendingRef.current = null;
    setPendingVisual(null);
    setStatus('idle');
    setError(null);
  }, []);

  const reportVisualError = useCallback((message: string) => {
    operationRef.current += 1;
    setStatus(pendingRef.current ? 'ready' : 'error');
    setUploadingUri(null);
    setError(message);
  }, []);

  const stageVisualAsset = useCallback(async ({ uri, mimeType: reportedMimeType, originalName }: StageVisualAssetInput): Promise<PendingVisualInput | null> => {
    const mimeType = resolveMimeType(uri, reportedMimeType);
    if (!mimeType) {
      setStatus('error');
      setError('Не удалось определить формат изображения. Выберите JPEG, PNG или WebP.');
      return null;
    }

    const operation = ++operationRef.current;
    const previous = pendingRef.current;
    setStatus('uploading');
    setUploadingUri(uri);
    setError(null);
    try {
      const uploaded = await api.uploadVisual(uri, mimeType, originalName ?? undefined);
      if (!mountedRef.current || operation !== operationRef.current) {
        try { await api.deletePendingVisual(uploaded.id); } catch { }
        return null;
      }

      const next: PendingVisualInput = {
        assetId: uploaded.id,
        localUri: uri,
        mimeType: uploaded.mimeType,
        sizeBytes: uploaded.sizeBytes,
      };
      pendingRef.current = next;
      setPendingVisual(next);
      setStatus('ready');
      setUploadingUri(null);
      if (previous && previous.assetId !== next.assetId) {
        void api.deletePendingVisual(previous.assetId).catch(() => undefined);
      }
      return next;
    } catch (err) {
      if (!mountedRef.current || operation !== operationRef.current) return null;
      setStatus(previous ? 'ready' : 'error');
      setUploadingUri(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить изображение.');
      return null;
    }
  }, []);

  const pickVisual = useCallback(async (source: VisualSource) => {
    const operation = ++operationRef.current;
    setStatus('picking');
    setError(null);

    try {
      if (source === 'camera' || source === 'selfie') {
        const permission = await ImagePicker.requestCameraPermissionsAsync();
        if (!permission.granted) {
          if (mountedRef.current && operation === operationRef.current) {
            setStatus(pendingRef.current ? 'ready' : 'error');
            setError('Разрешите доступ к камере, чтобы сделать фото.');
          }
          return;
        }
        const result = await ImagePicker.launchCameraAsync({
          mediaTypes: ['images'],
          cameraType: source === 'selfie' ? ImagePicker.CameraType.front : ImagePicker.CameraType.back,
          quality: 0.85,
        });
        if (result.canceled) {
          if (mountedRef.current && operation === operationRef.current) setStatus(pendingRef.current ? 'ready' : 'idle');
          return;
        }
        const asset = result.assets[0];
        if (operation === operationRef.current) {
          await stageVisualAsset({ uri: asset.uri, mimeType: asset.mimeType, originalName: asset.fileName });
        }
        return;
      }

      if (source === 'gallery') {
        const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'], quality: 0.85 });
        if (result.canceled) {
          if (mountedRef.current && operation === operationRef.current) setStatus(pendingRef.current ? 'ready' : 'idle');
          return;
        }
        const asset = result.assets[0];
        if (operation === operationRef.current) {
          await stageVisualAsset({ uri: asset.uri, mimeType: asset.mimeType, originalName: asset.fileName });
        }
        return;
      }

      const result = await DocumentPicker.getDocumentAsync({
        type: 'image/*',
        copyToCacheDirectory: true,
        multiple: false,
      });
      if (result.canceled) {
        if (mountedRef.current && operation === operationRef.current) setStatus(pendingRef.current ? 'ready' : 'idle');
        return;
      }
      const asset = result.assets[0];
      if (operation === operationRef.current) {
        await stageVisualAsset({ uri: asset.uri, mimeType: asset.mimeType, originalName: asset.name });
      }
    } catch (err) {
      if (!mountedRef.current || operation !== operationRef.current) return;
      setStatus(pendingRef.current ? 'ready' : 'error');
      setError(err instanceof Error ? err.message : 'Не удалось выбрать изображение.');
    }
  }, [stageVisualAsset]);

  const removePendingVisual = useCallback(async () => {
    const pending = pendingRef.current;
    if (!pending) return;
    const operation = ++operationRef.current;
    try {
      await api.deletePendingVisual(pending.assetId);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось удалить изображение.';
      // A race with a completed server turn means the image is already safe
      // in history; locally it should no longer appear as a pending input.
      if (!message.includes('уже прикреплено')) {
        if (mountedRef.current && operation === operationRef.current) {
          setStatus('ready');
          setError(message);
        }
        return;
      }
    }
    if (!mountedRef.current || operation !== operationRef.current) return;
    pendingRef.current = null;
    setPendingVisual(null);
    setStatus('idle');
    setUploadingUri(null);
    setError(null);
  }, []);

  return {
    pendingVisual,
    status,
    error,
    uploadingUri,
    getPendingVisual,
    consumePendingVisual,
    reportVisualError,
    stageVisualAsset,
    pickVisual,
    removePendingVisual,
  };
}
