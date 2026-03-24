import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

export function activate(context: vscode.ExtensionContext): void {
  const config = vscode.workspace.getConfiguration("zscript");
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
    documentSelector: [{ scheme: "file", language: "zscript" }],
  };

  client = new LanguageClient(
    "zscript",
    "ZScript Language Server",
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
