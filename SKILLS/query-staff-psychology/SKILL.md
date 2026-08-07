---
name: query-staff-psychology
description: 
---

# Get the full psychological profile and RPG stats of a staff member by name or role.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/query_staff_psychology \
  -H "Content-Type: application/json" \
  -d '{
  "staffId": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/query_staff_psychology -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/query_staff_psychology \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "staffId": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `staffId` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "staffId": {
      "type": "string"
    }
  },
  "required": [
    "staffId"
  ]
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "$ref": "#/$defs/StaffPsychologyResult"
    }
  },
  "$defs": {
    "StaffPsychologyResult": {
      "type": "object",
      "properties": {
        "Found": {
          "type": "boolean"
        },
        "StaffName": {
          "type": "string"
        },
        "Role": {
          "type": "string"
        },
        "Charisma": {
          "type": "number"
        },
        "Negotiation": {
          "type": "number"
        },
        "Discretion": {
          "type": "number"
        },
        "Stress": {
          "type": "number"
        },
        "Satisfaction": {
          "type": "number"
        },
        "Trauma": {
          "type": "number"
        },
        "MaxSatisfaction": {
          "type": "number"
        },
        "EffectivenessRating": {
          "type": "number"
        },
        "IsBurningOut": {
          "type": "boolean"
        },
        "IsQuitRisk": {
          "type": "boolean"
        },
        "DominantTraumaSource": {
          "type": "string"
        },
        "BreakRiskAssessment": {
          "type": "string"
        }
      },
      "required": [
        "Found",
        "Charisma",
        "Negotiation",
        "Discretion",
        "Stress",
        "Satisfaction",
        "Trauma",
        "MaxSatisfaction",
        "EffectivenessRating",
        "IsBurningOut",
        "IsQuitRisk"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

