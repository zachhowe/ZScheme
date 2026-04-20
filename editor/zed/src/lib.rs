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
        let path = worktree
            .shell_env()
            .into_iter()
            .find(|(k, _)| k == "ZSCHEME_LSP_PATH")
            .map(|(_, v)| v)
            .unwrap_or_else(|| "zs-lsp".to_string());

        Ok(zed::Command {
            command: path,
            args: vec![],
            env: Default::default(),
        })
    }
}

zed::register_extension!(ZSchemeExtension);
