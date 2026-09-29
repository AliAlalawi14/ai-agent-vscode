import * as vscode from "vscode";
import type { ConfiguredProvider, ProviderSetupRequest, SetupPreset } from "../shared/protocol";
import { PROVIDER_KEYS } from "./BackendProcess";
import { PROVIDER_PRESETS, fetchModels, providerSecretKey, type ProviderEntry } from "./providerPresets";

/**
 * Adding and removing model providers, shared by the chat panel's setup form and the command-palette
 * commands. Keys go to VS Code SecretStorage; the provider list to the `aiChat.providers` setting.
 */

const SETTING = "providers";

/** Claude and DeepSeek have native clients in the backend: only a key is needed, the models come from its config. */
const BUILTIN_DETAILS: Record<string, { detail: string; keyUrl: string }> = {
  anthropic: { detail: "Claude models", keyUrl: "https://console.anthropic.com/settings/keys" },
  deepseek: { detail: "Low-cost coding models", keyUrl: "https://platform.deepseek.com/api_keys" },
};

export function readProviders(): ProviderEntry[] {
  return vscode.workspace.getConfiguration("aiChat").get<ProviderEntry[]>(SETTING, []) ?? [];
}

export async function writeProviders(providers: ProviderEntry[]): Promise<void> {
  await vscode.workspace.getConfiguration("aiChat").update(SETTING, providers, vscode.ConfigurationTarget.Global);
}

/** Names the built-in providers use, so an added provider can't shadow them. */
export const RESERVED_NAMES = PROVIDER_KEYS.map((p) => p.id as string);

/** `base`, or `base-2`, `base-3`... when that name is taken. */
export function uniqueName(base: string, existing: ProviderEntry[]): string {
  const taken = new Set([...RESERVED_NAMES, ...existing.map((p) => p.name)]);
  if (!taken.has(base)) {return base;}
  let n = 2;
  while (taken.has(`${base}-${n}`)) {n++;}
  return `${base}-${n}`;
}

export function setupPresets(): SetupPreset[] {
  const builtin: SetupPreset[] = PROVIDER_KEYS.map((p) => ({
    id: p.id,
    label: p.label,
    detail: BUILTIN_DETAILS[p.id]?.detail ?? "",
    keyUrl: BUILTIN_DETAILS[p.id]?.keyUrl,
    kind: "builtin",
    auth: "bearer",
    baseUrl: "",
  }));
  const openai: SetupPreset[] = PROVIDER_PRESETS.map((p) => ({
    id: p.id,
    label: p.label,
    detail: p.detail,
    keyUrl: p.keyUrl,
    kind: "openai",
    auth: p.auth,
    baseUrl: p.baseUrl,
    needsResource: p.baseUrl.includes("{resource}"),
    local: p.auth === "none",
  }));
  const custom: SetupPreset = {
    id: "custom",
    label: "Custom server",
    detail: "Any OpenAI-compatible API: vLLM, DeepInfra, a company gateway…",
    kind: "custom",
    auth: "bearer",
    baseUrl: "",
  };
  return [...builtin, ...openai, custom];
}

export async function configuredProviders(secrets: vscode.SecretStorage): Promise<ConfiguredProvider[]> {
  const result: ConfiguredProvider[] = [];
  for (const p of PROVIDER_KEYS) {
    if (await secrets.get(p.secret)) {
      result.push({ name: p.id, label: p.label, models: [], removable: true });
    }
  }
  const legacy = vscode.workspace.getConfiguration("aiChat.openai");
  const legacyUrl = legacy.get<string>("baseUrl", "")?.trim();
  const legacyModels = legacy.get<string[]>("models", []) ?? [];
  if (legacyUrl && legacyModels.length > 0) {
    result.push({ name: legacy.get<string>("providerName", "openai") || "openai", label: legacyUrl, models: legacyModels, removable: false });
  }
  for (const p of readProviders()) {
    const label = PROVIDER_PRESETS.find((preset) => preset.baseUrl === p.baseUrl)?.label ?? p.baseUrl;
    result.push({ name: p.name, label, models: p.models, removable: true });
  }
  return result;
}

/** The base URL and auth a setup request resolves to, or why it can't. */
export function resolveEndpoint(
  request: ProviderSetupRequest,
): { baseUrl: string; auth: ProviderEntry["auth"] } | { error: string } {
  if (request.presetId === "custom") {
    const url = request.baseUrl?.trim() ?? "";
    if (!/^https?:\/\/\S+$/.test(url)) {return { error: "Enter the server's base URL, e.g. https://my-server.example.com/v1" };}
    return { baseUrl: url.replace(/\/+$/, ""), auth: request.auth ?? "bearer" };
  }
  const preset = PROVIDER_PRESETS.find((p) => p.id === request.presetId);
  if (!preset) {return { error: `Unknown provider '${request.presetId}'.` };}
  if (preset.baseUrl.includes("{resource}")) {
    const resource = request.resource?.trim() ?? "";
    if (!/^[a-zA-Z0-9-]+$/.test(resource)) {return { error: "Enter your Azure OpenAI resource name (the part before .openai.azure.com)." };}
    return { baseUrl: preset.baseUrl.replace("{resource}", resource), auth: preset.auth };
  }
  return { baseUrl: preset.baseUrl, auth: preset.auth };
}

/** The provider's live model list, so names are never outdated. */
export async function listProviderModels(request: ProviderSetupRequest): Promise<{ models: string[]; error?: string }> {
  const endpoint = resolveEndpoint(request);
  if ("error" in endpoint) {return { models: [], error: endpoint.error };}
  if (endpoint.auth !== "none" && !request.key?.trim()) {return { models: [], error: "Paste the API key first." };}
  return fetchModels(endpoint.baseUrl, endpoint.auth, request.key?.trim());
}

/** Stores the key and the provider. Throws a readable error when the request is incomplete. */
export async function saveProvider(
  context: vscode.ExtensionContext,
  request: ProviderSetupRequest,
  models: string[],
): Promise<void> {
  const key = request.key?.trim();
  const builtin = PROVIDER_KEYS.find((p) => p.id === request.presetId);
  if (builtin) {
    if (!key) {throw new Error("Paste the API key first.");}
    await context.secrets.store(builtin.secret, key);
    return;
  }

  const endpoint = resolveEndpoint(request);
  if ("error" in endpoint) {throw new Error(endpoint.error);}
  if (endpoint.auth !== "none" && !key) {throw new Error("Paste the API key first.");}
  const chosen = models.map((m) => m.trim()).filter((m) => m.length > 0);
  if (chosen.length === 0) {throw new Error("Pick at least one model.");}

  const existing = readProviders();
  const name = uniqueName(request.presetId, existing);
  if (key && endpoint.auth !== "none") {
    await context.secrets.store(providerSecretKey(name), key);
  }
  await writeProviders([
    ...existing,
    { name, baseUrl: endpoint.baseUrl, auth: endpoint.auth, models: chosen, ...(request.presetId === "openai" ? { useMaxCompletionTokens: true } : {}) },
  ]);
}

export async function removeProvider(context: vscode.ExtensionContext, name: string): Promise<void> {
  const builtin = PROVIDER_KEYS.find((p) => p.id === name);
  if (builtin) {
    await context.secrets.delete(builtin.secret);
    return;
  }
  await context.secrets.delete(providerSecretKey(name));
  await writeProviders(readProviders().filter((p) => p.name !== name));
}
