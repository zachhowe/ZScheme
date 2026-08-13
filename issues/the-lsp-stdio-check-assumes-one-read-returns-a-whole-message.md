# The LSP stdio check assumes one read returns a whole framed message

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `scripts/Test-Toolchain.ps1:240-264`, the "zs-lsp speaks LSP through
the shim" check.

## Symptom

The end-to-end script fails against a shim that is working perfectly:

```
no result in the response: 'Content-Length: 1234

'
```

or, when nothing came back at all, prints something that is not what was read:

```
no LSP framing in the response: 'a?'
```

## Root cause

Two problems in the same three lines.

**One read is assumed to be the whole message.** The check does a single
`ReadAsync` and asserts against whatever it returns:

```powershell
$read = $process.StandardOutput.ReadAsync($buffer, 0, $buffer.Length)
if (-not $read.Wait(60000)) { throw "no response within 60s" }

$response = -join $buffer[0..($read.Result - 1)]
if ($response -notmatch 'Content-Length:') { throw "no LSP framing in the response: '$response'" }
if ($response -notmatch '"result"') { throw "no result in the response: '$response'" }
```

A stream read returns what is available, not what was asked for. The language
server writes the header and the body — plausibly with a flush between them — so
a read that returns only `Content-Length: N\r\n\r\n` satisfies the framing
assertion and fails the `"result"` one. That is a false failure on a good build,
in the one automated check for a redirected-handle mistake in the Windows shim,
and it points the reader at the shim rather than at the reader.

**A zero-byte read prints garbage.** If `$read.Result` is `0`,
`$buffer[0..($read.Result - 1)]` is `$buffer[0..-1]`, and PowerShell resolves a
descending range against an array by wrapping: it yields element `0` *and* the
last element, not an empty slice.

```
PS> $b = [char[]]'abcdef'; -join $b[0..-1]
af
```

So the "no output at all" case — the most interesting failure this check can
find — reports two arbitrary characters of an uninitialised 4096-char buffer
instead of saying the server wrote nothing.

## Suggested fix direction

1. **Accumulate until the assertions can be answered**, bounded by the same
   60-second budget rather than per read: loop on `ReadAsync`, append to a
   `StringBuilder`, and stop as soon as the text contains both `Content-Length:`
   and `"result"`. A `Task.Wait` with the remaining budget keeps the existing
   "no response within 60s" failure intact.
2. **Guard the slice.** `if ($read.Result -gt 0) { -join $buffer[0..($read.Result - 1)] } else { '' }`,
   so an empty read reports as empty. Worth doing even after step 1, since the
   loop still slices each read.

## Priority note

Low: a developer-facing script, not shipped code, and the framing race may never
have been hit in practice. It earns a fix because both defects push in the
expensive direction — one turns a working shim into a red build, the other
corrupts the diagnostic for the failure the check exists to catch.
