/**
 * OpenAI-compatible providers the "Add Provider" wizard knows. The base URL is the FULL base including the
 * version path (the backend calls <base>/chat/completions and <base>/models). "{resource}" is asked for.
 * Model names aren't listed here: the wizard reads them live from the provider's /models endpoint.
 */
export interface ProviderPreset {
  id: string;
  label: string;
  baseUrl: string;
  auth: "bearer" | "api-key" | "none";
  detail: string;
  /** Where to create a key (opened from the chat panel's setup form) */
  keyUrl?: string;
}

export const PROVIDER_PRESETS: ProviderPreset[] = [
  { id: "gemini", label: "Google Gemini", baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai", auth: "bearer", detail: "Key from Google AI Studio", keyUrl: "https://aistudio.google.com/apikey" },
  { id: "mistral", label: "Mistral", baseUrl: "https://api.mistral.ai/v1", auth: "bearer", detail: "Key from console.mistral.ai", keyUrl: "https://console.mistral.ai/api-keys" },
  { id: "xai", label: "xAI (Grok)", baseUrl: "https://api.x.ai/v1", auth: "bearer", detail: "Key from console.x.ai", keyUrl: "https://console.x.ai" },
  { id: "groq", label: "Groq", baseUrl: "https://api.groq.com/openai/v1", auth: "bearer", detail: "Fast open models", keyUrl: "https://console.groq.com/keys" },
  { id: "openrouter", label: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1", auth: "bearer", detail: "One key, hundreds of models", keyUrl: "https://openrouter.ai/keys" },
  { id: "together", label: "Together AI", baseUrl: "https://api.together.xyz/v1", auth: "bearer", detail: "Open models", keyUrl: "https://api.together.ai/settings/api-keys" },
  { id: "fireworks", label: "Fireworks AI", baseUrl: "https://api.fireworks.ai/inference/v1", auth: "bearer", detail: "Open models", keyUrl: "https://fireworks.ai/account/api-keys" },
  { id: "openai", label: "OpenAI", baseUrl: "https://api.openai.com/v1", auth: "bearer", detail: "Key from platform.openai.com", keyUrl: "https://platform.openai.com/api-keys" },
  { id: "azure", label: "Azure OpenAI", baseUrl: "https://{resource}.openai.azure.com/openai/v1", auth: "api-key", detail: "Your Azure resource name + key", keyUrl: "https://portal.azure.com" },
  { id: "ollama", label: "Ollama (local)", baseUrl: "http://localhost:11434/v1", auth: "none", detail: "Runs on your machine, no key; code never leaves it" },
  { id: "lmstudio", label: "LM Studio (local)", baseUrl: "http://localhost:1234/v1", auth: "none", detail: "Runs on your machine, no key" },
];

/** A provider as stored in the `aiChat.providers` setting (its key lives in SecretStorage, never here). */
export interface ProviderEntry {
  name: string;
  baseUrl: string;
  auth: "bearer" | "api-key" | "none";
  models: string[];
  useMaxCompletionTokens?: boolean;
}

export const providerSecretKey = (name: string): string => `aiChat.key.provider.${name}`;

/** Model ids from an OpenAI-compatible `GET <base>/models` (empty when the provider doesn't offer the list). */
export async function listModels(baseUrl: string, auth: ProviderEntry["auth"], key: string | undefined): Promise<string[]> {
  return (await fetchModels(baseUrl, auth, key)).models;
}

/** Like listModels, plus why the list is empty (rejected key, unreachable server...), for the setup form. */
export async function fetchModels(
  baseUrl: string,
  auth: ProviderEntry["auth"],
  key: string | undefined,
): Promise<{ models: string[]; error?: string }> {
  const headers: Record<string, string> = {};
  if (key && auth === "bearer") {
    headers.Authorization = `Bearer ${key}`;
  } else if (key && auth === "api-key") {
    headers["api-key"] = key;
  }
  try {
    const response = await fetch(`${baseUrl.replace(/\/+$/, "")}/models`, { headers, signal: AbortSignal.timeout(10_000) });
    if (response.status === 401 || response.status === 403) {
      return { models: [], error: `The provider rejected the key (HTTP ${response.status}).` };
    }
    if (!response.ok) {
      return { models: [], error: `The provider didn't return a model list (HTTP ${response.status}). Type the model ids instead.` };
    }
    const data = (await response.json()) as { data?: Array<{ id?: string }> };
    const models = (data.data ?? []).map((m) => m.id ?? "").filter((id) => id.length > 0).sort();
    return models.length > 0 ? { models } : { models, error: "The provider returned no models. Type the model ids instead." };
  } catch (error) {
    const local = /localhost|127\.0\.0\.1/.test(baseUrl);
    return {
      models: [],
      error: local
        ? `Nothing answered at ${baseUrl}. Is the local server running?`
        : `Couldn't reach ${baseUrl}: ${error instanceof Error ? error.message : "network error"}`,
    };
  }
}
