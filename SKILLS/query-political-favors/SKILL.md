---
name: query-political-favors
description: 
---

# Get current favor levels with Precinct Captain, DA, and Commissioner. Shows available zoning permits.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/query_political_favors \
  -H "Content-Type: application/json" \
  -d '{}'
```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/query_political_favors \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{}'
```

## Input

This tool takes no input parameters.

### Input JSON Schema

```json
{
  "type": "object",
  "additionalProperties": false
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "$ref": "#/$defs/PoliticalFavorResult"
    }
  },
  "$defs": {
    "System.String-1": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "PoliticalFavorResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "PrecinctFavor": {
          "type": "number"
        },
        "DAFavor": {
          "type": "number"
        },
        "CommissionerFavor": {
          "type": "number"
        },
        "TotalAllocation": {
          "type": "number"
        },
        "ZoningUnlocked": {
          "type": "boolean"
        },
        "AvailablePermits": {
          "$ref": "#/$defs/System.String-1"
        },
        "Message": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "PrecinctFavor",
        "DAFavor",
        "CommissionerFavor",
        "TotalAllocation",
        "ZoningUnlocked"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

