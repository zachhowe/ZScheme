# Mock Testing Guidelines

## Philosophy

Mocks in this project follow a "no logic" principle. They exist to:
1. Record which methods were called and with what parameters
2. Return configurable results for testing different scenarios
3. Provide controlled inputs to verify how the subject under test responds

Mocks should NOT contain business logic or make decisions.

## Core Patterns

### 1. Call Recording with Lists

Record method calls using typed lists for easy verification:

```csharp
// From MockNuGetV3Client
public List<string> GetVersionsCalls { get; } = [];
public List<(string PackageId, string Version, string DestinationPath)> DownloadCalls { get; } = [];
public int GetPackageBaseAddressCalls { get; private set; }

public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId)
{
    GetVersionsCalls.Add(packageId);
    // ... return result
}

public Task DownloadNupkgAsync(string packageId, string version, string destinationPath)
{
    DownloadCalls.Add((packageId, version, destinationPath));
    // ... return result
}
```

In tests:
```csharp
var call = Assert.Single(_client.DownloadCalls);
Assert.Equal("TestPackage", call.PackageId);
Assert.Equal("1.0.0", call.Version);

Assert.Contains(_client.GetVersionsCalls, c => c == "TransitiveDep");
Assert.DoesNotContain("Child", _client.GetVersionsCalls);
```

### 2. Configurable Results

Use dictionaries and callbacks to control what the mock returns:

```csharp
// From MockNuGetV3Client
public Dictionary<string, IReadOnlyList<string>> Versions { get; } =
    new(StringComparer.OrdinalIgnoreCase);

public Action<string, string, string>? OnDownload { get; set; }

public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId)
{
    GetVersionsCalls.Add(packageId);

    if (Versions.TryGetValue(packageId, out var versions))
        return Task.FromResult(versions);

    return Task.FromResult<IReadOnlyList<string>>([]);
}

public Task DownloadNupkgAsync(string packageId, string version, string destinationPath)
{
    DownloadCalls.Add((packageId, version, destinationPath));
    OnDownload?.Invoke(packageId, version, destinationPath);
    return Task.CompletedTask;
}
```

In tests:
```csharp
// Configure available versions for a package
_client.Versions["TransitiveDep"] = ["1.0.0", "1.1.0", "2.0.0"];

// Configure download behavior with a callback
_client.OnDownload = (id, version, path) =>
{
    if (id == "RootPackage")
        WriteNupkg(path, id, version,
        [
            new NuspecDependencyRef("TransitiveA", "2.0.0")
        ]);
    else
        WriteNupkg(path, id, version, []);
};

// Configure error scenario
_client.OnDownload = (_, _, _) =>
    throw new HttpRequestException("Network error");
```

### 3. Input Queuing

For mocks that simulate interactive input, use a queue that returns items in order:

```csharp
// From MockReplConsole
public Queue<string?> Inputs { get; } = new();
public List<string> WrittenText { get; } = [];
public List<string> WrittenLines { get; } = [];
public List<string> ErrorLines { get; } = [];

public string? ReadLine()
{
    return Inputs.Count > 0 ? Inputs.Dequeue() : null;
}

public void Write(string text)
{
    WrittenText.Add(text);
}

public void WriteErrorLine(string text)
{
    ErrorLines.Add(text);
}
```

In tests:
```csharp
// Queue up a sequence of inputs
_console.Inputs.Enqueue("(define x 42)");
_console.Inputs.Enqueue("x");
_console.Inputs.Enqueue(":quit");
CreateRepl().Run();

// Verify output
Assert.Contains(_console.WrittenLines, l => l.Contains(": Int"));
Assert.Empty(_console.ErrorLines);
```

### 4. ClearTracking Method

Always provide a `ClearTracking()` method for test isolation between test cases:

```csharp
// From MockNuGetV3Client
public void ClearTracking()
{
    GetVersionsCalls.Clear();
    DownloadCalls.Clear();
    GetPackageBaseAddressCalls = 0;
}

// From MockReplConsole
public void ClearTracking()
{
    Inputs.Clear();
    WrittenText.Clear();
    WrittenLines.Clear();
    ErrorLines.Clear();
}
```

### 5. Pre-configured Data Dictionaries

For methods that return data, use dictionaries that tests can populate:

```csharp
// From MockNuGetV3Client
public Dictionary<string, IReadOnlyList<string>> Versions { get; } =
    new(StringComparer.OrdinalIgnoreCase);

public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId)
{
    GetVersionsCalls.Add(packageId);

    if (Versions.TryGetValue(packageId, out var versions))
        return Task.FromResult(versions);

    return Task.FromResult<IReadOnlyList<string>>([]);
}
```

In tests:
```csharp
// Set up test data
_client.Versions["TransitiveDep"] = ["1.0.0", "1.1.0", "2.0.0"];

// Now GetVersionsAsync("TransitiveDep") will return these versions
var result = await graph.ResolveAsync(roots);

Assert.Contains(result, r => r.Id == "TransitiveDep" && r.Version == "1.1.0");
```

## Reference Examples

- **MockNuGetV3Client** (`tests/ZScheme.Compiler.Tests/Package/MockNuGetV3Client.cs`) — Comprehensive example with call recording, configurable results via dictionaries and callbacks, and ClearTracking
- **MockReplConsole** (`tests/ZScheme.Compiler.Tests/Repl/MockReplConsole.cs`) — Input queuing, output recording, and ClearTracking
