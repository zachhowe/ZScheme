use zed_extension_api::{self as zed, Result};

struct ZSchemeExtension;

impl zed::Extension for ZSchemeExtension {
    fn new() -> Self {
        ZSchemeExtension
    }

    fn language_server_command(
        &mut self,
        _language_server_id: &zed::LanguageServerId,
        worktree: &zed::Worktree,
    ) -> Result<zed::Command> {
        let env = worktree.shell_env();
        let lookup = |name: &str| {
            env.iter()
                .find(|(k, _)| k == name)
                .map(|(_, v)| v.to_string())
        };

        let path = lookup("ZSCHEME_LSP_PATH").unwrap_or_else(|| "zs-lsp".to_string());

        // Extra arguments, whitespace separated — e.g. ZSCHEME_LSP_ARGS="--debug" turns on
        // the server's verbose logging, which Zed captures into its own log.
        let args = lookup("ZSCHEME_LSP_ARGS")
            .map(|raw| raw.split_whitespace().map(str::to_string).collect())
            .unwrap_or_default();

        Ok(zed::Command {
            command: path,
            args,
            env: Default::default(),
        })
    }
}

zed::register_extension!(ZSchemeExtension);
