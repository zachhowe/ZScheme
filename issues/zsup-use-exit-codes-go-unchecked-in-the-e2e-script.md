# `zsup use` exit codes go unchecked in the end-to-end script

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `scripts/Test-Toolchain.ps1:167` and `:171`, in the "install a second
toolchain and switch between them" check.

## Symptom

A failing `zsup use` is reported as a resolver bug:

```
expected the second toolchain, got 'C:\Users\me\.zscheme\toolchains\0.4.0\bin\zs.exe'
```

The reader goes looking at toolchain selection — `ZSCHEME_VERSION`, the
`.zscheme-version` lookup, the settings file — when the actual failure was the
command two lines earlier that never set the default at all, and whose own error
message scrolled past above.

## Root cause

Every other native call in the script checks its exit code. These two do not:

```powershell
& $zsup use "0.0.1-e2e"
$resolved = & $zsup which zs 2>$null
if ($resolved -notmatch '0\.0\.1-e2e') { throw "expected the second toolchain, got '$resolved'" }

& $zsup use $version
$resolved = & $zsup which zs 2>$null
if ($resolved -notmatch [regex]::Escape($version)) { throw "expected $version, got '$resolved'" }
```

Compare the install eleven lines above, which is written the intended way:

```powershell
& $zsup install "0.0.1-e2e" --from $toolchainArchive --no-default
if ($LASTEXITCODE -ne 0) { throw "second install exited $LASTEXITCODE" }
```

`zsup use` fails for real reasons the script can provoke — an unwritable
`settings.json`, a name the registry cannot find — and the `which` assertion
downstream converts every one of them into the same misleading message.

## Suggested fix direction

Add the check both scripts already use, naming the toolchain so the two sites are
distinguishable:

```powershell
& $zsup use "0.0.1-e2e"
if ($LASTEXITCODE -ne 0) { throw "use 0.0.1-e2e exited $LASTEXITCODE" }
```

While there: `& $zsup which zs 2>$null` swallows stderr on both lines, so a
`which` that fails also arrives as an empty `$resolved` and the same assertion
message. Worth checking its exit code too, or dropping the `2>$null` now that the
failure would be reported directly.

## Priority note

Low: a developer-facing script, and the check still fails — it just blames the
wrong component. Two lines to fix, and it removes a misdirection from the one
test that proves toolchain switching works.
