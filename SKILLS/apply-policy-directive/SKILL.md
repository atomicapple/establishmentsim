---
name: apply-policy-directive
description: 
---

# Enact a policy from the Panderer's Code. Tier-0 policies lock a branch permanently. Keys: WF0–WF3 (Workforce Protection), SE0–SE3 (Systemic Exploitation).

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/apply_policy_directive \
  -H "Content-Type: application/json" \
  -d '{
  "directiveId": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/apply_policy_directive -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/apply_policy_directive \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "directiveId": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `directiveId` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "directiveId": {
      "type": "string"
    }
  },
  "required": [
    "directiveId"
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
      "$ref": "#/$defs/PolicyResult"
    }
  },
  "$defs": {
    "PolicyResult": {
      "type": "object",
      "properties": {
        "Success": {
          "type": "boolean"
        },
        "DirectiveId": {
          "type": "string"
        },
        "PolicyName": {
          "type": "string"
        },
        "Tier": {
          "type": "integer"
        },
        "Branch": {
          "type": "string"
        },
        "Effects": {
          "type": "string"
        },
        "Message": {
          "type": "string"
        },
        "ActiveBranch": {
          "type": "string"
        },
        "TotalEnactedPolicies": {
          "type": "integer"
        },
        "ModifierSummary": {
          "type": "string"
        }
      },
      "required": [
        "Success",
        "Tier",
        "TotalEnactedPolicies"
      ]
    }
  },
  "required": [
    "result"
  ]
}
```

