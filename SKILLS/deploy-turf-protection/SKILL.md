---
name: deploy-turf-protection
description: 
---

# Pay security to protect a district. Reduces rival syndicate aggression by 15.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/deploy_turf_protection \
  -H "Content-Type: application/json" \
  -d '{
  "districtId": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/deploy_turf_protection -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/deploy_turf_protection \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "districtId": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `districtId` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "districtId": {
      "type": "string"
    }
  },
  "required": [
    "districtId"
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
      "$ref": "#/$defs/TurfProtectionResult"
    }
  },
  "$defs": {
    "System.String-1": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "TurfProtectionResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "DistrictId": {
          "type": "string"
        },
        "Cost": {
          "type": "number"
        },
        "RivalAggressionReduction": {
          "type": "number"
        },
        "AffectedSyndicates": {
          "$ref": "#/$defs/System.String-1"
        },
        "Message": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "Cost",
        "RivalAggressionReduction"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

