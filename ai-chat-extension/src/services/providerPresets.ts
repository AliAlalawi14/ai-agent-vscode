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
}

export const PROVIDER_PRESETS: ProviderPreset[] = [
  { id: "gemini", label: "Google Gemini", baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai", auth: "bearer", detail: "Key from Google AI Studio" },
  { id: "mistral", label: "Mistral", baseUrl: "https://api.mistral.ai/v1", auth: "bearer", detail: "Key from console.mistral.ai" },
  { id: "xai", label: "xAI (Grok)", baseUrl: "https://api.x.ai/v1", auth: "bearer", detail: "Key from console.x.ai" },
  { id: "groq", label: "Groq", baseUrl: "https://api.groq.com/openai/v1", auth: "bearer", detail: "Fast open models" },
  { id: "openrouter", label: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1", auth: "bearer", detail: "One key, hundreds of models" },
  { id: "together", label: "Together AI", baseUrl: "https://api.together.xyz/v1", auth: "bearer", detail: "Open models" },
  { id: "fireworks", label: "Fireworks AI", baseUrl: "https://api.fireworks.ai/inference/v1", auth: "bearer", detail: "Open models" },
  { id: "openai", label: "OpenAI", baseUrl: "https://api.openai.com/v1", auth: "bearer", detail: "Key from platform.openai.com" },
  { id: "azure", label: "Azure OpenAI", baseUrl: "https://{resource}.openai.azure.com/openai/v1", auth: "api-key", detail: "Your Azure resource name + key" },
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
  const headers: Record<string, string> = {};
  if (key && auth === "bearer") {
    headers.Authorization = `Bearer ${key}`;
  } else if (key && auth === "api-key") {
    headers["api-key"] = key;
  }
  try {
    const response = await fetch(`${baseUrl.replace(/\/+$/, "")}/models`, { headers, signal: AbortSignal.timeout(10_000) });
    if (!response.ok) {
      return [];
    }
    const data = (await response.json()) as { data?: Array<{ id?: string }> };
    return (data.data ?? []).map((m) => m.id ?? "").filter((id) => id.length > 0).sort();
  } catch {
    return [];
  }
}
