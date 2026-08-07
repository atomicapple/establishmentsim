---
name: execute-blackmail-extortion
description: 
---

# Extort cash from a target by burning a Capital Favor. Cash amount scales with favor rarity.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/execute_blackmail_extortion \
  -H "Content-Type: application/json" \
  -d '{
  "targetId": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/execute_blackmail_extortion -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/execute_blackmail_extortion \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "targetId": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `targetId` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "targetId": {
      "type": "string"
    }
  },
  "required": [
    "targetId"
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
      "$ref": "#/$defs/BlackmailResult"
    }
  },
  "$defs": {
    "BlackmailResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "TargetId": {
          "type": "string"
        },
        "CashExtorted": {
          "type": "number"
        },
        "HeatReduction": {
          "type": "number"
        },
        "FavorsRemaining": {
          "type": "integer"
        },
        "FavorUsed": {
          "type": "string"
        },
        "Message": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "CashExtorted",
        "HeatReduction",
        "FavorsRemaining"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

