# The shim's child runs before it is assigned to the kill-on-close job, and a refused assignment is silent

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; the window is short and no live repro was attempted.

**Affects:** `zs` and `zs-lsp` launched through the shim on Windows only — Unix
takes the `execv` path (`ShimRunner.cs:58-65`) and has no job object at all.

## Symptom

The leak `WindowsJobObject` exists to prevent, in two residual forms:

- A grandchild spawned by `zs`/`zs-lsp` in the first few milliseconds of its life
  survives the shim's death, because it was created before the job existed around
  its parent.
- If assignment is refused outright, *nothing* is reaped and *nothing* is
  reported: the editor terminates the shim, the real `zs-lsp` keeps running and
  keeps holding the workspace, and one more leaks on every editor restart — the
  exact scenario `WindowsJobObject`'s doc comment (`src/ZScheme.Zsup/WindowsJobObject.cs:9-16`)
  describes as the reason the class exists.

## Root cause

`ShimRunner.Launch` (`src/ZScheme.Zsup/ShimRunner.cs:84-99`) starts the child
*running* and assigns it afterwards:

```csharp
using var job = WindowsJobObject.TryCreate();
using var child = Process.Start(psi);        // <- child begins executing here
if (child is null) { … }

try
{
    job?.TryAssign(child.Handle);            // <- return value discarded
}
catch (InvalidOperationException)
{
    // The child exited before we could assign it, so there is nothing left to reap.
}
```

Two separate defects.

**The ordering window.** Between `Process.Start` returning and
`AssignProcessToJobObject` being called, the child is a normal unparented process.
Anything it spawns in that window is created outside the job and does not inherit
it, so it is not covered by `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The child itself
is covered from the moment of assignment, so the leak is limited to
fast-spawning grandchildren — `zs` shelling out to a compiler or an
`import-clr` helper during startup is the plausible shape, and it is admittedly
narrow.

**The discarded `bool`.** `TryAssign` (`WindowsJobObject.cs:71-75`) is explicitly
best-effort and returns false rather than throwing:

```csharp
/// <summary>Best-effort assignment; returns false if the process could not be added.</summary>
internal bool TryAssign(IntPtr processHandle)
{
    return _handle != IntPtr.Zero && AssignProcessToJobObject(_handle, processHandle);
}
```

`ShimRunner` ignores that result. `TryCreate` returning null is a documented,
tolerated degradation ("the shim still works, it just loses the guarantee that the
child is reaped", `:30-33`) — but a *successful* create followed by a *refused*
assign is a different and more surprising state, and it is indistinguishable from
success at the call site. `AssignProcessToJobObject` fails when the process is
already in an incompatible job, which is the normal condition inside some CI
containers and under a few process-supervision tools, so this is not a
never-happens branch.

## Suggested fix direction

1. **Report the refused assignment.** One line, no restructuring, and it converts
   a silent leak into something diagnosable:

   ```csharp
   if (job is not null && !job.TryAssign(child.Handle))
       ZsupHelpers.Warn("could not tie the child to this process; it may outlive the shim");
   ```

   The `InvalidOperationException` catch must stay — `child.Handle` throws it when
   the child has already exited, which is benign — and the warning has to be
   inside the same guard so a normal fast exit does not produce it. Worth doing
   even if nothing below gets done. It should not be *noisy*: `zs-lsp` runs under
   an editor whose stderr the user may never see, so consider whether this belongs
   behind a debug/verbose flag instead.

2. **Close the ordering window by starting the child suspended.** This is the real
   fix and the reason this is an issue rather than a one-line change:
   `Process.Start` cannot do it. It needs a direct `CreateProcessW` P/Invoke with
   `CREATE_SUSPENDED`, then `AssignProcessToJobObject`, then `ResumeThread` — and
   that means owning, on the Windows path only:

   - the command line, quoted per `CommandLineToArgvW` rules (today `ArgumentList`
     does this, and the comment at `:76-77` notes it is what keeps paths with
     spaces working);
   - the environment block, built from the inherited environment plus the
     overrides `ChildEnvironment` supplies (`:120-146`);
   - handle inheritance, which is load-bearing — the comment at `:69-72` records
     that the child must inherit the console handles directly, because `zs-lsp`
     speaks JSON-RPC over stdio and `zs repl` reads the console;
   - `PROCESS_INFORMATION` handle cleanup, and a `WaitForSingleObject` +
     `GetExitCodeProcess` to replace `WaitForExit`/`ExitCode`.

   That is a meaningful amount of new unmanaged code on the hottest path in the
   product — every single `zs` invocation goes through it — to close a window
   measured in milliseconds. It should not be done casually, and it needs its own
   tests on the console-inheritance and argument-quoting behaviour, which is
   precisely what the current code gets for free from `ProcessStartInfo`.

3. A cheaper partial alternative to (2): assign the *shim itself* to the job
   before starting the child, so the child inherits job membership at creation.
   This works because job membership is inherited by default, and it needs no new
   P/Invoke — but it also means the shim kills itself when the job closes, so the
   `Dispose` ordering and the exit-code path (`:106-114`) both need rethinking.
   Probably the best value of the three; worth prototyping before committing to
   (2).

## Priority note

Lowest of the zsup review findings, and correctly so — the child process, which is
what actually leaks in the reported scenario, *is* covered once assignment
succeeds. What is left is a narrow grandchild window and an unreported failure
mode. Item 1 is cheap and should happen; items 2 and 3 need a decision about how
much unmanaged process-launch code the shim should own, and that decision is worth
more than the bug.
