import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

export function activate(context: vscode.ExtensionContext): void {
  const config = vscode.workspace.getConfiguration("zscheme");
  const serverPath = config.get<string>("languageServer.path", "");
  const serverArgs = config.get<string[]>("languageServer.args", []);

  if (!serverPath) {
    return;
  }

  const serverOptions: ServerOptions = {
    command: serverPath,
    args: serverArgs,
  };

  const clientOptions: LanguageClientOptions = {
    // Manifests are served too — AnalysisService dispatches on the .zspkg suffix.
    // "zscheme-fmt" is deliberately absent: the server has no handler for .zsfmt.
    documentSelector: [
      { scheme: "file", language: "zscheme" },
      { scheme: "file", language: "zscheme-package" },
    ],
  };

  client = new LanguageClient(
    "zscheme",
    "ZScheme Language Server",
    serverOptions,
    clientOptions
  );

  client.start();
}

export function deactivate(): Thenable<void> | undefined {
  if (!client) {
    return undefined;
  }
  return client.stop();
}
