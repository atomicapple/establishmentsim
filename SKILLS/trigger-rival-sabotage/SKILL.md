---
name: trigger-rival-sabotage
description: 
---

# Sabotage a rival syndicate's operations. Costs cash, reduces their power by 15 and aggression by 10.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/trigger_rival_sabotage \
  -H "Content-Type: application/json" \
  -d '{
  "rivalId": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/trigger_rival_sabotage -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/trigger_rival_sabotage \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "rivalId": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `rivalId` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "rivalId": {
      "type": "string"
    }
  },
  "required": [
    "rivalId"
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
      "$ref": "#/$defs/RivalSabotageResult"
    }
  },
  "$defs": {
    "RivalSabotageResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "RivalId": {
          "type": "string"
        },
        "Cost": {
          "type": "number"
        },
        "PowerReduction": {
          "type": "number"
        },
        "NewPower": {
          "type": "number"
        },
        "RespectChange": {
          "type": "number"
        },
        "Message": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "Cost",
        "PowerReduction",
        "NewPower",
        "RespectChange"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

