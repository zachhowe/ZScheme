# Mock Testing Guidelines

## Philosophy

Mocks in this project follow a "no logic" principle. They exist to:
1. Record which methods were called and with what parameters
2. Return configurable results for testing different scenarios
3. Trigger events to verify how the subject under test responds

Mocks should NOT contain business logic or make decisions.

## Core Patterns

### 1. Call Recording with Tuples

Record method calls using `List<(...)>` tuples for easy verification:

```csharp
// From MockQuestManager
public List<(Guid CharacterId, string QuestId)> AcceptedQuests { get; } = new();
public List<(Guid CharacterId, string QuestId)> CompletedQuests { get; } = new();
public List<(Guid CharacterId, string QuestId, string ObjectiveId, object NewValue)> ObjectiveUpdates { get; } = new();

public Task<QuestOperationResult> AcceptQuestAsync(Guid characterId, string questId)
{
    AcceptedQuests.Add((characterId, questId));
    // ... return result
}
```

In tests:
```csharp
Assert.Single(mockQuestManager.AcceptedQuests);
Assert.Equal(("quest-1", playerId), mockQuestManager.AcceptedQuests[0]);
```

### 2. Configurable Results

Use `NextXxxResult` properties that reset after use to avoid test pollution:

```csharp
// From MockQuestManager
public QuestAcceptanceResult? NextAcceptanceResult { get; set; }
public QuestOperationResult? NextOperationResult { get; set; }

public Task<QuestAcceptanceResult> CanAcceptQuestAsync(Guid characterId, string questId)
{
    var result = NextAcceptanceResult ?? new QuestAcceptanceResult(true, null);
    NextAcceptanceResult = null;  // Reset after use
    return Task.FromResult(result);
}
```

In tests:
```csharp
// Configure failure scenario
mockQuestManager.NextAcceptanceResult = new QuestAcceptanceResult(false, "Quest already completed");

// Call the method under test - it will receive the failure result
var result = await questService.TryAcceptQuest(playerId, "quest-1");

// Next call will get the default success result (auto-reset)
```

### 3. Event Triggering

Provide `TriggerXxxAsync()` helpers to manually fire events for testing subscribers:

```csharp
// From MockQuestManager
public event AsyncEventHandler<QuestAcceptedEventArgs>? OnQuestAccepted;
public event AsyncEventHandler<QuestCompletedEventArgs>? OnQuestCompleted;

public async Task TriggerQuestAcceptedAsync(Guid characterId, Quest quest)
{
    if (OnQuestAccepted != null)
    {
        await OnQuestAccepted.InvokeAsync(this, new QuestAcceptedEventArgs(characterId, quest));
    }
}

public async Task TriggerQuestCompletedAsync(Guid characterId, Quest quest)
{
    if (OnQuestCompleted != null)
    {
        await OnQuestCompleted.InvokeAsync(this, new QuestCompletedEventArgs(characterId, quest, quest.Reward));
    }
}
```

In tests:
```csharp
// Subscribe to events
var receivedEvents = new List<QuestAcceptedEventArgs>();
mockQuestManager.OnQuestAccepted += (sender, args) => {
    receivedEvents.Add(args);
    return Task.CompletedTask;
};

// Trigger the event
await mockQuestManager.TriggerQuestAcceptedAsync(playerId, testQuest);

// Verify the subscriber received it
Assert.Single(receivedEvents);
```

### 4. ClearTracking Method

Always provide a `ClearTracking()` method for test isolation between test cases:

```csharp
// From MockQuestManager
public void ClearTracking()
{
    RegisteredQuests.Clear();
    QuestsByNpc.Clear();
    AcceptedQuests.Clear();
    CompletedQuests.Clear();
    AbandonedQuests.Clear();
    ObjectiveUpdates.Clear();
    ActiveQuests.Clear();
    CompletedQuestsByCharacter.Clear();
}
```

In tests:
```csharp
[Fact]
public async Task Test1()
{
    mockQuestManager.ClearTracking();  // Ensure clean state
    // ... test code
}
```

### 5. Pre-configured Data Dictionaries

For methods that return data, use dictionaries that tests can populate:

```csharp
// From MockQuestManager
public Dictionary<Guid, List<QuestProgress>> ActiveQuests { get; } = new();

public Task<List<QuestProgress>> GetActiveQuestsAsync(Guid characterId)
{
    var quests = ActiveQuests.GetValueOrDefault(characterId) ?? new List<QuestProgress>();
    return Task.FromResult(quests);
}
```

In tests:
```csharp
// Set up test data
mockQuestManager.ActiveQuests[playerId] = new List<QuestProgress> { testProgress };

// Now GetActiveQuestsAsync will return this data
var result = await service.GetPlayerQuests(playerId);
```

## Reference Examples

- **MockQuestManager** (`test/ZWorld.GameServer.Tests/Mocks/MockQuestManager.cs`) - Comprehensive example with all patterns: call recording, configurable results, event triggering, data dictionaries
- **MockDialogueManager** (`test/ZWorld.GameServer.Tests/Mocks/MockDialogueManager.cs`) - Simpler example focused on call recording and session storage
