export function buildMessageRequest(prompt) {
  return {
    model: "claude-opus-5",
    max_tokens: 128000,
    output_config: { effort: "high" },
    messages: [{ role: "user", content: prompt }],
  };
}