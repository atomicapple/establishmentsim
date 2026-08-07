# Establishment Simulator — AI Coding Conventions

> This file is pinned at conversation index 0 and locked for prefix caching.
> Do not change it mid-session — it anchors the KV-cache stable region.

## Project Identity

- **Engine:** Godot 4.7.1 (C# / .NET 8)
- **Assembly:** `EstablishmentSimulator`
- **NuGet:** `com.IvanMurzak.ReflectorNet` v5.3.2, `com.IvanMurzak.McpPlugin` v7.2.0
- **MCP Bridge:** SignalR → `localhost:8080/hub/mcp-server`

## C# Coding Standards

### Naming
- **Classes / Structs:** PascalCase (`GameStateManager`, `HeatSystem`)
- **Methods:** PascalCase (`ApplyShiftWorkload`, `GetMonthlySummary`)
- **Properties:** PascalCase (`Cash`, `IsBurningOut`)
- **Private fields:** `_camelCase` (`_cash`, `_stress`)
- **Constants:** PascalCase (`MaxRounds`, `BreakThreshold`)
- **Enums:** PascalCase both type and members (`PolicyBranch.WorkforceProtection`)
- **Signals:** PascalCase delegate (`OnDailyTickEventHandler`), PascalCase signal name (`SignalName.OnDailyTick`)
- **Local variables:** camelCase (`heatReduction`, `staffName`)

### File Structure
```
One primary class per file. Related DTOs/enums in same file.
Order: using → enums → DTOs/structs → main class.
```

### Godot Conventions
- **Node scripts:** `partial class` inheriting from `Node` / `Control` / `Resource`
- **Autoloads:** Registered in `project.godot` → `[autoload]` section
- **Signals:** `[Signal] public delegate void NameEventHandler(params);` → emit via `EmitSignal(SignalName.Name, args)`
- **Exports:** `[Export]` on public properties for Inspector visibility
- **Resources:** `[GlobalClass] partial class` inheriting from `Resource`
- **Editor-only code:** `#if TOOLS` guard

### Patterns
- **Singleton access:** `GameStateManager.Instance` (set in `_Ready()`, guard against duplicates)
- **Scene-tree lookup:** `GetTree()?.Root?.FindChild("Name", recursive: true, owned: false) as T`
- **Deferred connection:** `CallDeferred(nameof(ConnectToSystems))` for cross-system wiring
- **Signals for cross-system communication** — no direct references between unrelated systems
- **Clamp all bounded values:** `Mathf.Clamp(value, 0f, 100f)`
- **Use `Mathf.IsEqualApprox` for float comparisons, `Math.Abs` for double**

### MCP Tool Registration
- `[AiToolType]` on static tool classes
- `[AiTool("name", "title")]` on static methods
- Return structured DTOs (plain objects with public get/set properties)
- ReflectorNet handles serialization automatically

## Project Architecture

```
GameStateManager (Autoload Node)  ←  Central metrics hub
├── HeatSystem (Node)             ←  Police scrutiny & raids
├── FinancialLedger (Node)        ←  Revenue & OPEX tracking
├── PsychologicalBreakSystem      ←  Staff mental breaks
├── PolicyTreeManager (Node)      ←  Panderer's Code policy tree
├── VenueGridManager (Node)       ←  Room layout & synergies
├── ClientNegotiationHandler      ←  Haggling state machine
├── EmergentContextEmitter         ←  LLM narrative bridge
├── EventDialogUI (Control)       ←  AI event dialog renderer
└── GameMcpTools (static)          ←  MCP tool endpoints

StaffMember (Resource)            ←  Employee stats & agency
RoomModule (Resource)             ←  Grid room definition
PolicyDefinition (Resource)       ←  Policy tree node
```

## Safety Rules
1. **Never mutate `GameStateManager.Instance` from a non-main thread.**
2. **Always clamp agency variables (Stress, Satisfaction, Trauma) to 0–100.**
3. **Always check `GameStateManager.Instance != null` before access.**
4. **Psychological break events must always emit a signal before applying effects.**
5. **Policy enactment must check prerequisites, cooldown, and branch exclusivity.**
6. **Financial transactions must update both the ledger AND `GameStateManager.Cash`.**
7. **MCP tool methods must be static and idempotent where possible.**
