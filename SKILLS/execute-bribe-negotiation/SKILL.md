---
name: execute-bribe-negotiation
description: 
---

# Bribe a police officer to reduce establishment Heat. Higher heat reduces effectiveness.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/execute_bribe_negotiation \
  -H "Content-Type: application/json" \
  -d '{
  "officerId": "string_value",
  "amount": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/execute_bribe_negotiation -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/execute_bribe_negotiation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "officerId": "string_value",
  "amount": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `officerId` | `string` | Yes |  |
| `amount` | `number` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "officerId": {
      "type": "string"
    },
    "amount": {
      "type": "number"
    }
  },
  "required": [
    "officerId",
    "amount"
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
      "$ref": "#/$defs/BribeResult"
    }
  },
  "$defs": {
    "BribeResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "OfficerId": {
          "type": "string"
        },
        "AmountPaid": {
          "type": "number"
        },
        "HeatBefore": {
          "type": "number"
        },
        "HeatAfter": {
          "type": "number"
        },
        "HeatReduction": {
          "type": "number"
        },
        "RemainingCash": {
          "type": "number"
        },
        "Message": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "AmountPaid",
        "HeatBefore",
        "HeatAfter",
        "HeatReduction",
        "RemainingCash"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

