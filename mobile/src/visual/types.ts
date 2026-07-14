export type VisualSource = 'camera' | 'selfie' | 'gallery' | 'file';

export interface PendingVisualInput {
  assetId: string;
  localUri: string;
  mimeType: string;
  sizeBytes: number;
  originalName: string | null;
}

export interface StageVisualAssetInput {
  uri: string;
  mimeType?: string | null;
  originalName?: string | null;
}

export type VisualInputStatus = 'idle' | 'picking' | 'uploading' | 'ready' | 'error';
