---
name: capture-exception
description: 
---

# Capture and record a runtime exception for debugging

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/capture_exception \
  -H "Content-Type: application/json" \
  -d '{
  "message": "string_value",
  "stackTrace": "string_value",
  "source": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8080/api/tools/capture_exception -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/capture_exception \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "message": "string_value",
  "stackTrace": "string_value",
  "source": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `message` | `string` | Yes |  |
| `stackTrace` | `string` | Yes |  |
| `source` | `string` | Yes |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "message": {
      "type": "string"
    },
    "stackTrace": {
      "type": "string"
    },
    "source": {
      "type": "string"
    }
  },
  "required": [
    "message",
    "stackTrace",
    "source"
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
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

