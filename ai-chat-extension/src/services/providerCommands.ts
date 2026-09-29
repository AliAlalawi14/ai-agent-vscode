import * as vscode from "vscode";
import { BackendProcess } from "./BackendProcess";
import { PROVIDER_PRESETS, listModels, providerSecretKey } from "./providerPresets";
import { RESERVED_NAMES, readProviders, removeProvider, uniqueName, writeProviders } from "./providerSetup";

/**
 * "Stoat: Add Provider": preset (or custom URL) → key (secret storage) → models, read live from the provider.
 * "Stoat: Remove Provider". Both restart the built-in backend so the model picker updates.
 */
export function registerProviderCommands(context: vscode.ExtensionContext, backend: BackendProcess): vscode.Disposable[] {
  const restart = async () => {
    if (!BackendProcess.isExternal()) {
      await backend.restart();
    }
  };

  const add = vscode.commands.registerCommand("aiChat.addProvider", async () => {
    const pick = await vscode.window.showQuickPick(
      [
        ...PROVIDER_PRESETS.map((p) => ({ label: p.label, description: p.detail, preset: p })),
        { label: "Custom OpenAI-compatible server…", description: "vLLM, DeepInfra, your company's gateway…", preset: undefined },
      ],
      { placeHolder: "Which provider? (any OpenAI-compatible API works)", matchOnDescription: true },
    );
    if (!pick) {
      return;
    }

    let baseUrl = pick.preset?.baseUrl ?? "";
    if (baseUrl.includes("{resource}")) {
      const resource = await vscode.window.showInputBox({ prompt: "Azure OpenAI resource name (the part before .openai.azure.com)", ignoreFocusOut: true });
      if (!resource) {
        return;
      }
      baseUrl = baseUrl.replace("{resource}", resource.trim());
    }
    if (!pick.preset) {
      const url = await vscode.window.showInputBox({
        prompt: "Base URL including the version path (the agent calls <base>/chat/completions)",
        placeHolder: "https://my-server.example.com/v1",
        ignoreFocusOut: true,
        validateInput: (v) => (/^https?:\/\/\S+$/.test(v.trim()) ? undefined : "Enter an http(s) URL"),
      });
      if (!url) {
        return;
      }
      baseUrl = url.trim();
    }

    const existing = readProviders();
    const suggested = pick.preset?.id ?? "custom";
    const name = await vscode.window.showInputBox({
      prompt: "A short name for this provider (shown in the model picker)",
      value: uniqueName(suggested, existing),
      ignoreFocusOut: true,
      validateInput: (v) =>
        !/^[a-z0-9][a-z0-9-]*$/.test(v.trim())
          ? "Lowercase letters, digits and dashes"
          : RESERVED_NAMES.includes(v.trim()) || existing.some((p) => p.name === v.trim())
            ? "That name is already used"
            : undefined,
    });
    if (!name) {
      return;
    }

    const auth = pick.preset?.auth ?? ((await vscode.window.showQuickPick(
      [
        { label: "Bearer token", auth: "bearer" as const },
        { label: "api-key header (Azure style)", auth: "api-key" as const },
        { label: "No key (local server)", auth: "none" as const },
      ],
      { placeHolder: "How does this server take its key?" },
    ))?.auth);
    if (!auth) {
      return;
    }

    let key: string | undefined;
    if (auth !== "none") {
      key = await vscode.window.showInputBox({ prompt: `${pick.label} API key (stored in VS Code's secret storage)`, password: true, ignoreFocusOut: true });
      if (!key) {
        return;
      }
      key = key.trim();
    }

    // Read the model list live, so names are never outdated; fall back to typing them
    const available = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: `Reading ${pick.label} models…` },
      () => listModels(baseUrl, auth, key),
    );
    let models: string[] = [];
    if (available.length > 0) {
      const chosen = await vscode.window.showQuickPick(available, {
        canPickMany: true,
        placeHolder: "Pick the models to offer (they must support tool calling for Agent/Plan mode)",
        ignoreFocusOut: true,
      });
      models = chosen ?? [];
    } else {
      const typed = await vscode.window.showInputBox({
        prompt: "Model ids, comma separated (the provider didn't return a list)",
        placeHolder: "e.g. qwen3:14b",
        ignoreFocusOut: true,
      });
      models = (typed ?? "").split(",").map((m) => m.trim()).filter((m) => m.length > 0);
    }
    if (models.length === 0) {
      vscode.window.showWarningMessage("Stoat: no model picked, so the provider wasn't added.");
      return;
    }

    if (key) {
      await context.secrets.store(providerSecretKey(name.trim()), key);
    }
    await writeProviders([
      ...existing,
      { name: name.trim(), baseUrl, auth, models, ...(pick.preset?.id === "openai" ? { useMaxCompletionTokens: true } : {}) },
    ]);
    await restart();
    vscode.window.showInformationMessage(`Stoat: added ${pick.label} (${models.length} model${models.length === 1 ? "" : "s"}). Pick it in the chat's model menu.`);
  });

  const remove = vscode.commands.registerCommand("aiChat.removeProvider", async () => {
    const providers = readProviders();
    if (providers.length === 0) {
      vscode.window.showInformationMessage("Stoat: no providers added with 'Add Provider' yet.");
      return;
    }
    const pick = await vscode.window.showQuickPick(
      providers.map((p) => ({ label: p.name, description: `${p.baseUrl} · ${p.models.join(", ")}` })),
      { placeHolder: "Remove which provider?" },
    );
    if (!pick) {
      return;
    }
    await removeProvider(context, pick.label);
    await restart();
  });

  return [add, remove];
}
