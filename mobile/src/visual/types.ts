export type VisualSource = 'camera' | 'selfie' | 'gallery' | 'file';

export interface PendingVisualInput {
  assetId: string;
  localUri: string;
  mimeType: string;
  sizeBytes: number;
}

export interface StageVisualAssetInput {
  uri: string;
  mimeType?: string | null;
  originalName?: string | null;
}

export type VisualInputStatus = 'idle' | 'picking' | 'uploading' | 'ready' | 'error';
