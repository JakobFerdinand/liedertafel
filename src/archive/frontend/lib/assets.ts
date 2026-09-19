import { postAuth } from "@/lib/auth";
import type { LiedAsset, LiedRevision } from "@/lib/songs";

export type AssetResponse = LiedAsset & {
  musicalVersionId: string;
  createdAt: string;
};

export type UploadSessionResponse = {
  uploadSessionId: string;
  uploadUrl: string;
  expiresAt: string;
  maxBytes: number;
};

export type RevisionResponse = LiedRevision & {
  assetId: string;
};

export type AssetAccessResponse = {
  assetId: string;
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  viewUrl: string;
  downloadUrl: string;
  expiresAt: string;
};

export async function createAsset(
  versionId: string,
  body: { assetType?: string; voiceLabel?: string | null } = {},
): Promise<AssetResponse> {
  const response = await postAuth(
    `/api/musical-versions/${encodeURIComponent(versionId)}/assets`,
    body,
  );
  if (!response.ok) throw response;
  return (await response.json()) as AssetResponse;
}

export async function createUploadSession(
  assetId: string,
): Promise<UploadSessionResponse> {
  const response = await postAuth(
    `/api/assets/${encodeURIComponent(assetId)}/upload-session`,
    {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as UploadSessionResponse;
}

export async function finalizeUpload(
  uploadSessionId: string,
): Promise<RevisionResponse> {
  const response = await postAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}/finalize`,
    {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as RevisionResponse;
}

export async function fetchAssetAccess(
  assetId: string,
  signal?: AbortSignal,
): Promise<AssetAccessResponse> {
  const response = await fetch(
    `/api/assets/${encodeURIComponent(assetId)}/access`,
    {
      credentials: "same-origin",
      cache: "no-store",
      signal,
    },
  );
  if (!response.ok) throw response;
  return (await response.json()) as AssetAccessResponse;
}
