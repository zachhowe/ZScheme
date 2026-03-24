use zed_extension_api::{self as zed, Result};

struct ZScriptExtension;

impl zed::Extension for ZScriptExtension {
    fn new() -> Self {
        ZScriptExtension
    }

    fn language_server_command(
        &mut self,
        _language_server_id: &zed::LanguageServerId,
        worktree: &zed::Worktree,
    ) -> Result<zed::Command> {
        let path = worktree
            .shell_env()
            .get("ZSCRIPT_LSP_PATH")
            .cloned()
            .unwrap_or_else(|| "ZScript.LanguageServer".to_string());

        Ok(zed::Command {
            command: path,
            args: vec![],
            env: Default::default(),
        })
    }
}

zed::register_extension!(ZScriptExtension);
