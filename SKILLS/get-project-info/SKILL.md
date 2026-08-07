---
name: get-project-info
description: 
---

# Get metadata about the current Godot project

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8080/api/tools/get_project_info \
  -H "Content-Type: application/json" \
  -d '{}'
```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8080/api/tools/get_project_info \
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
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

