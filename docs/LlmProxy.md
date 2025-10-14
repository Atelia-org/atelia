# The proxy server exposes the following endpoints:

## LLM服务聚合Proxy
url: `http://localhost:4000`

## OpenAI Compatible API
Chat Completions: `POST /openai/v1/chat/completions` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/openai/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

List Models: `GET /openai/v1/models`
```bash
curl http://localhost:4000/openai/v1/models
```

Retrieve Model: `GET /openai/v1/models/{model}`
```bash
curl http://localhost:4000/openai/v1/models/gpt-4.1
```

## Anthropic Compatible API
Messages: `POST /anthropic/v1/messages` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/anthropic/v1/messages \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

Count Tokens: `POST /anthropic/v1/messages/count_tokens` (counts the number of tokens in a message)
```bash
curl -X POST http://localhost:4000/anthropic/v1/messages/count_tokens \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello, Claude"}]
  }'
```

List Models: `GET /anthropic/v1/models`
Retrieve Model: `GET /anthropic/v1/models/{model}`

## Claude Code Compatible API
Messages: `POST /anthropic/claude/v1/messages` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/anthropic/claude/v1/messages \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

Count Tokens: `POST /anthropic/claude/v1/messages/count_tokens`
List Models: `GET /anthropic/claude/v1/models`
Retrieve Model: `GET /anthropic/claude/v1/models/{model}`

---

# 最终实际的内容结构

## SystemInstruction

**段落 1：身份定义**
```md
Your name is GitHub Copilot.
You are an AI programming assistant.
When asked for your name, you must respond with "GitHub Copilot".
```

```md
Follow Microsoft content policies. Avoid content that violates copyrights. If you are asked to generate content that is harmful, hateful, racist, sexist, lewd, or violent, only respond with "Sorry, I can't assist with that."
Keep your answers short and impersonal.
Use Markdown formatting in your answers. Make sure to include the programming language name at the start of the Markdown code blocks. Avoid wrapping the whole response in triple backticks.
Use KaTeX for math equations in your answers. Wrap inline math equations in $. Wrap more complex blocks of math equations in $$.
The user works in an IDE called Visual Studio Code which has a concept for editors with open files, integrated unit test support, an output pane that shows the output of running the code as well as an integrated terminal. The active document is the source code the user is looking at right now. You can only give one reply for each conversation turn.
```

```xml
<budget:token_budget>200000</budget:token_budget>
```

## 首条消息
```json
{"role": "user", "content": "[SYSTEM]{DeveloperInstruction}\n<<HUMAN_CONVERSATION_START>>\n{FirstUserContent}"}
```

